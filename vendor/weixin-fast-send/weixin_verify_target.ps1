<#
  Adapted from raphael2025/weixin-auto-send (MIT).
  Performs exactly one bounded Weixin 4.x recipient-verification pass:
  activate -> search -> OCR result list -> open unique match -> OCR title.
  It never pastes or sends the user's payload. HuiTian sends only after this
  process returns code=verified, preserving the user's complete clipboard.
#>
param(
  [string]$Contact,
  [switch]$SelfTest,
  [switch]$DiagnosticOnly,
  [string]$DiagnosticImagePath
)

$ProgressPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
$VerbosePreference = 'SilentlyContinue'
$DebugPreference = 'SilentlyContinue'
$ErrorActionPreference = 'Stop'
$helperTimer = [Diagnostics.Stopwatch]::StartNew()
$script:lastStageMs = 0
function Trace-Stage([string]$Name) {
  $elapsed = $helperTimer.ElapsedMilliseconds
  [Console]::Error.WriteLine(('stage={0};elapsedMs={1};durationMs={2}' -f $Name,$elapsed,($elapsed-$script:lastStageMs)))
  $script:lastStageMs = $elapsed
}
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch {}

function Write-Result([string]$Code, [bool]$Ok, [string]$Detail, $Extra = $null) {
  $value = [ordered]@{ code=$Code; ok=$Ok; detail=$Detail }
  if ($null -ne $Extra) {
    foreach ($property in $Extra.PSObject.Properties) { $value[$property.Name] = $property.Value }
  }
  [pscustomobject]$value | ConvertTo-Json -Depth 6 -Compress
}

function Normalize-Name([string]$Value) {
  if ($null -eq $Value) { return '' }
  return (($Value -replace '\s','') -replace '[，。；：、,.!！?？（）()【】\[\]_-]','').ToLowerInvariant()
}

function Get-Levenshtein([string]$Left, [string]$Right) {
  $leftValue=Normalize-Name $Left; $rightValue=Normalize-Name $Right
  if ($leftValue.Length -eq 0) { return $rightValue.Length }
  if ($rightValue.Length -eq 0) { return $leftValue.Length }
  $previous = New-Object 'int[]' ($rightValue.Length + 1)
  $current = New-Object 'int[]' ($rightValue.Length + 1)
  for($column=0;$column -le $rightValue.Length;$column++){ $previous[$column]=$column }
  for($row=1;$row -le $leftValue.Length;$row++){
    $current[0]=$row
    for($column=1;$column -le $rightValue.Length;$column++){
      $sub = if($leftValue[$row-1] -eq $rightValue[$column-1]){0}else{1}
      $current[$column]=[Math]::Min(
        [Math]::Min($current[$column-1]+1,$previous[$column]+1),
        $previous[$column-1]+$sub)
    }
    $swap=$previous;$previous=$current;$current=$swap
  }
  return $previous[$rightValue.Length]
}

function Get-MatchScore([string]$Target,[string]$Candidate) {
  $targetValue=Normalize-Name $Target; $candidateValue=Normalize-Name $Candidate
  if(-not $targetValue -or -not $candidateValue){ return 0.0 }
  if($targetValue -eq $candidateValue){ return 1.0 }
  if($candidateValue.Contains($targetValue)){
    return [Math]::Max(0.76, 0.96 - 0.025 * ($candidateValue.Length-$targetValue.Length))
  }
  if($targetValue.Contains($candidateValue) -and $candidateValue.Length -ge 2){ return 0.78 }
  $distance=Get-Levenshtein $targetValue $candidateValue
  $maximum=[Math]::Max($targetValue.Length,$candidateValue.Length)
  if($distance -eq 1 -and $maximum -ge 3){ return 0.76 }
  $targetChars=@($targetValue.ToCharArray() | Select-Object -Unique)
  $shared=@($targetChars | Where-Object { $candidateValue.Contains([string]$_) }).Count
  return [Math]::Min(0.69, 0.62 * $shared / [Math]::Max(1,$targetChars.Count))
}

function Get-NearCandidateRows($Ranked,[double]$BestScore) {
  $eligible=@($Ranked |
    Where-Object { $_.score -ge [Math]::Max(0.72,$BestScore-0.06) })

  # An exact visible name wins over longer fuzzy rows (for example an exact
  # contact followed by a group whose name merely contains the query). Two
  # exact names on genuinely different rows remain ambiguous.
  $exact=@($eligible|Where-Object{$_.score -ge 0.995})
  if($exact.Count -gt 0){$eligible=$exact}

  # Windows OCR can return two fragments for a single visual result row with
  # slightly different Y values. The former y/6 bucket made that one contact
  # look like two contacts. Cluster vertically overlapping fragments while
  # keeping separate rows (and therefore real duplicate names) distinct.
  $rows=@()
  foreach($candidate in @($eligible|Sort-Object {$_.line.cy},{$_.line.y})){
    $center=if($null-ne$candidate.line.cy){[double]$candidate.line.cy}else{[double]$candidate.line.y}
    $height=if($null-ne$candidate.line.h -and [double]$candidate.line.h -gt 0){[double]$candidate.line.h}else{12.0}
    $match=$null
    foreach($row in $rows){
      if($row.section -ne $candidate.section){continue}
      $rowCenter=if($null-ne$row.line.cy){[double]$row.line.cy}else{[double]$row.line.y}
      $rowHeight=if($null-ne$row.line.h -and [double]$row.line.h -gt 0){[double]$row.line.h}else{12.0}
      $tolerance=[Math]::Max(14.0,[Math]::Max($height,$rowHeight)*1.15)
      if([Math]::Abs($center-$rowCenter) -le $tolerance){$match=$row;break}
    }
    if($null-eq$match){$rows += $candidate;continue}
    if($candidate.score -gt $match.score){
      $index=[Array]::IndexOf([object[]]$rows,$match)
      if($index -ge 0){$rows[$index]=$candidate}
    }
  }
  return @($rows)
}

if(-not $SelfTest -and [string]::IsNullOrWhiteSpace($Contact)){
  Write-Result 'failed' $false '没有提供联系人。'
  exit 2
}

Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class HuiTianWeixinNative {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr value);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint left, uint right, bool attach);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

  static readonly IntPtr TopMost=new IntPtr(-1);
  static readonly IntPtr NotTopMost=new IntPtr(-2);
  const uint WindowFlags=0x0001|0x0002|0x0040;

  public static void EnableDpi(){
    try{ if(SetProcessDpiAwarenessContext(new IntPtr(-4))) return; }catch{}
    try{ SetProcessDpiAwareness(2); return; }catch{}
    try{ SetProcessDPIAware(); }catch{}
  }
  static bool IsQtWindowClass(string value){
    const string prefix="Qt";
    const string suffix="QWindowIcon";
    if(String.IsNullOrEmpty(value)||
       !value.StartsWith(prefix,StringComparison.Ordinal)||
       !value.EndsWith(suffix,StringComparison.Ordinal)) return false;
    int digitCount=value.Length-prefix.Length-suffix.Length;
    if(digitCount<=0) return false;
    for(int index=prefix.Length;index<prefix.Length+digitCount;index++){
      char character=value[index];
      if(character<'0'||character>'9') return false;
    }
    return true;
  }
  public static IntPtr FindMain(){
    var found=new List<IntPtr>();
    EnumWindows((h,l)=>{
      uint pid; GetWindowThreadProcessId(h,out pid);
      try{
        var process=System.Diagnostics.Process.GetProcessById((int)pid);
        if(!string.Equals(process.ProcessName,"Weixin",StringComparison.OrdinalIgnoreCase)) return true;
      }catch{return true;}
      var className=new StringBuilder(256); GetClassName(h,className,256);
      if(IsQtWindowClass(className.ToString())) found.Add(h);
      return true;
    },IntPtr.Zero);
    IntPtr best=IntPtr.Zero; long area=-1; int bestTitle=0;
    foreach(var h in found){
      RECT rect; GetWindowRect(h,out rect);
      int width=rect.Right-rect.Left, height=rect.Bottom-rect.Top;
      var title=new StringBuilder(256); GetWindowText(h,title,256);
      int titleScore=title.ToString()=="微信"?1:0;
      long candidateArea=(long)Math.Max(0,width)*Math.Max(0,height);
      if((width>=600&&height>=400)||IsIconic(h)){
        if(titleScore>bestTitle||(titleScore==bestTitle&&candidateArea>area)){
          best=h;area=candidateArea;bestTitle=titleScore;
        }
      }
    }
    if(best==IntPtr.Zero&&found.Count>0) best=found[0];
    return best;
  }
  public static void Activate(IntPtr h){
    if(IsIconic(h)){ShowWindow(h,9);System.Threading.Thread.Sleep(180);}
    IntPtr foreground=GetForegroundWindow(); uint pid;
    uint foregroundThread=GetWindowThreadProcessId(foreground,out pid);
    uint currentThread=GetCurrentThreadId(); bool attached=false;
    keybd_event(0x12,0,0,UIntPtr.Zero);keybd_event(0x12,0,2,UIntPtr.Zero);
    if(foregroundThread!=currentThread) attached=AttachThreadInput(currentThread,foregroundThread,true);
    ShowWindow(h,5);BringWindowToTop(h);SetForegroundWindow(h);
    SetWindowPos(h,TopMost,0,0,0,0,WindowFlags);
    if(attached)AttachThreadInput(currentThread,foregroundThread,false);
  }
  public static void Release(IntPtr h){if(h!=IntPtr.Zero)SetWindowPos(h,NotTopMost,0,0,0,0,WindowFlags);}
  public static void Ctrl(byte key){
    keybd_event(0x11,0,0,UIntPtr.Zero);keybd_event(key,0,0,UIntPtr.Zero);
    keybd_event(key,0,2,UIntPtr.Zero);keybd_event(0x11,0,2,UIntPtr.Zero);
  }
  public static void Press(byte key){keybd_event(key,0,0,UIntPtr.Zero);keybd_event(key,0,2,UIntPtr.Zero);}
  public static void ClickRestore(int x,int y){
    POINT prior;GetCursorPos(out prior);SetCursorPos(x,y);System.Threading.Thread.Sleep(45);
    mouse_event(0x0002,0,0,0,UIntPtr.Zero);mouse_event(0x0004,0,0,0,UIntPtr.Zero);
    System.Threading.Thread.Sleep(45);SetCursorPos(prior.X,prior.Y);
  }
}
'@ | Out-Null

try { [HuiTianWeixinNative]::EnableDpi() } catch {}
Trace-Stage 'native_ready'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
  $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
  $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]
function Await($operation,$resultType){
  $task=$asTaskGeneric.MakeGenericMethod($resultType).Invoke($null,@($operation))
  if(-not $task.Wait(2500)){throw '本地文字识别阶段未及时返回，已停止核对；没有发送。'}
  return $task.Result
}
[Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine,Windows.Media,ContentType=WindowsRuntime] | Out-Null
[Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime] | Out-Null
$language=New-Object Windows.Globalization.Language('zh-Hans-CN')
$ocrEngine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language)
if($null -eq $ocrEngine){$ocrEngine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()}
if($null -eq $ocrEngine){
  Write-Result 'ocr_unavailable' $false 'Windows 简体中文 OCR 组件不可用。'
  exit 3
}
Trace-Stage 'ocr_ready'

function Get-OcrLines([int]$Left,[int]$Top,[int]$Width,[int]$Height,[double]$Scale){
  if($Width -lt 4 -or $Height -lt 4){return @()}
  $bitmap=$null;$graphics=$null;$large=$null;$largeGraphics=$null;$stream=$null;$softwareBitmap=$null;$memory=$null
  try{
    $bitmap=New-Object System.Drawing.Bitmap($Width,$Height)
    $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
    if($SelfTest){
      # Synthetic pixels only: no desktop read, activation, clipboard or input.
      $graphics.Clear([Drawing.Color]::White)
      $fixtureFont=New-Object Drawing.Font('Microsoft YaHei',24)
      try{$graphics.DrawString('回天测试 123',$fixtureFont,[Drawing.Brushes]::Black,10,10)}finally{$fixtureFont.Dispose()}
    }else{
      $graphics.CopyFromScreen($Left,$Top,0,0,(New-Object System.Drawing.Size($Width,$Height)))
    }
    $Scale=[Math]::Min($Scale,[Windows.Media.Ocr.OcrEngine]::MaxImageDimension/[double][Math]::Max($Width,$Height))
    $scaledWidth=[int]($Width*$Scale);$scaledHeight=[int]($Height*$Scale)
    $large=New-Object System.Drawing.Bitmap($scaledWidth,$scaledHeight)
    $largeGraphics=[System.Drawing.Graphics]::FromImage($large)
    $largeGraphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $largeGraphics.DrawImage($bitmap,0,0,$scaledWidth,$scaledHeight)
    # Keep pixels in memory; the old path encoded PNG, wrote disk, reopened
    # StorageFile, then decoded again on every crop.
    $memory=New-Object System.IO.MemoryStream
    $large.Save($memory,[System.Drawing.Imaging.ImageFormat]::Bmp)
    $memory.Position=0
    if($DiagnosticOnly -and -not [string]::IsNullOrWhiteSpace($DiagnosticImagePath)){
      $diagnosticDirectory=[System.IO.Path]::GetDirectoryName($DiagnosticImagePath)
      if(-not [string]::IsNullOrWhiteSpace($diagnosticDirectory)){
        [System.IO.Directory]::CreateDirectory($diagnosticDirectory)|Out-Null
      }
      $large.Save($DiagnosticImagePath,[System.Drawing.Imaging.ImageFormat]::Png)
    }
    $stream=[System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($memory)
    $decoder=Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $softwareBitmap=Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    Trace-Stage 'pixels_ready'
    $result=Await ($ocrEngine.RecognizeAsync($softwareBitmap)) ([Windows.Media.Ocr.OcrResult])
    Trace-Stage 'ocr_complete'
    $lines=@()
    foreach($line in $result.Lines){
      $minimumX=1e9;$minimumY=1e9;$maximumX=0;$maximumY=0
      foreach($word in $line.Words){
        $bounds=$word.BoundingRect
        if($bounds.X -lt $minimumX){$minimumX=$bounds.X};if($bounds.Y -lt $minimumY){$minimumY=$bounds.Y}
        if(($bounds.X+$bounds.Width) -gt $maximumX){$maximumX=$bounds.X+$bounds.Width}
        if(($bounds.Y+$bounds.Height) -gt $maximumY){$maximumY=$bounds.Y+$bounds.Height}
      }
      if($minimumX -eq 1e9){continue}
      $absoluteX=$Left+$minimumX/$Scale;$absoluteY=$Top+$minimumY/$Scale
      $lineWidth=($maximumX-$minimumX)/$Scale;$lineHeight=($maximumY-$minimumY)/$Scale
      $lines += [pscustomobject]@{
        text=($line.Text -replace '\s','');x=[int]$absoluteX;y=[int]$absoluteY
        w=[int]$lineWidth;h=[int]$lineHeight
        cx=[int]($absoluteX+$lineWidth/2);cy=[int]($absoluteY+$lineHeight/2)
      }
    }
    return @($lines)
  } finally {
    if($null-ne$softwareBitmap){$softwareBitmap.Dispose()};if($null-ne$stream){$stream.Dispose()}
    if($null-ne$largeGraphics){$largeGraphics.Dispose()};if($null-ne$large){$large.Dispose()}
    if($null-ne$graphics){$graphics.Dispose()};if($null-ne$bitmap){$bitmap.Dispose()}
    if($null-ne$memory){$memory.Dispose()}
  }
}

function Select-UniqueTarget($Lines,[string]$Target){
  $sectionHeaders=@(
    '最常使用','联系人','群聊','聊天记录','聊天文件','功能','公众号','小程序','朋友圈','群公告',
    '搜索网络结果','网络搜索','搜一搜','最近在搜','搜索建议','收藏'
  )
  $allowed=@('最常使用','联系人','群聊')
  $bad=@(
    '聊天记录','聊天文件','功能','公众号','小程序','朋友圈','群公告',
    '搜索网络结果','网络搜索','搜一搜','最近在搜','搜索建议','收藏'
  )
  $ordered=@($Lines|Sort-Object y)
  $sawGood=@($ordered|Where-Object{$allowed -contains (Normalize-Name $_.text)}).Count -gt 0
  $section='';$candidates=@()
  foreach($line in $ordered){
    $text=Normalize-Name $line.text
    if(-not $text){continue}
    if($sectionHeaders -contains $text){$section=$text;continue}
    if($text.StartsWith('查看全部') -or $text.StartsWith('搜索') -or $text.Contains('相关聊天记录')){continue}
    if(($bad -contains $section) -and -not ($allowed -contains $section)){continue}
    if($sawGood -and -not ($allowed -contains $section)){continue}
    $score=Get-MatchScore $Target $line.text
    if($score -ge 0.62){
      # OCR may expose duplicate text fragments on the same visual row. Collapse
      # only the same row; two contacts with the same name at different Y
      # coordinates remain separate and therefore ambiguous.
      $rowBucket=[Math]::Round(([double]$line.y)/6.0)
      $candidates += [pscustomobject]@{
        line=$line;score=$score;key=$text;section=$section
        rowKey="$section|$text|$rowBucket"
      }
    }
  }
  if($candidates.Count -eq 0){return [pscustomobject]@{code='not_found';best=$null;candidates=@()}}

  # Weixin 4.x may show one exact contact followed by several groups whose
  # secondary text contains the same query. Those are different result kinds,
  # not duplicate contacts. Resolve one section at a time: an explicit contact
  # always wins; only when no contact matches do we consider frequent contacts,
  # and only then groups. Ambiguity is still preserved within the chosen section.
  $contactSection=Normalize-Name '联系人'
  $frequentSection=Normalize-Name '最常使用'
  $groupSection=Normalize-Name '群聊'
  $contactCandidates=@($candidates|Where-Object{$_.section -eq $contactSection})
  $frequentCandidates=@($candidates|Where-Object{$_.section -eq $frequentSection})
  $groupCandidates=@($candidates|Where-Object{$_.section -eq $groupSection})
  $selectedPool=if($contactCandidates.Count -gt 0){
    $contactCandidates
  }elseif($frequentCandidates.Count -gt 0){
    $frequentCandidates
  }elseif($groupCandidates.Count -gt 0){
    $groupCandidates
  }else{
    @()
  }
  if($selectedPool.Count -eq 0){return [pscustomobject]@{code='not_found';best=$null;candidates=@()}}
  $ranked=@($selectedPool|Sort-Object @{Expression='score';Descending=$true})
  $best=$ranked[0]
  if($best.score -lt 0.72){return [pscustomobject]@{code='not_found';best=$best;candidates=$ranked}}
  $near=Get-NearCandidateRows $ranked $best.score
  if($near.Count -gt 1){return [pscustomobject]@{code='ambiguous';best=$best;candidates=$near}}
  return [pscustomobject]@{code='ok';best=$best;candidates=$near}
}

if($SelfTest){
  $fixtureLines=@(Get-OcrLines 0 0 420 100 1.5)
  $fixtureText=($fixtureLines|ForEach-Object{$_.text}) -join ''
  $exact=Get-MatchScore '凉雨' '凉雨'
  $different=Get-MatchScore '凉雨' '凉风'
  $duplicateNames=@(
    [pscustomobject]@{score=1.0;rowKey='联系人|凉雨|100';line=[pscustomobject]@{text='凉雨';y=100}},
    [pscustomobject]@{score=1.0;rowKey='联系人|凉雨|200';line=[pscustomobject]@{text='凉雨';y=200}}
  )
  $distinctRows=@(Get-NearCandidateRows $duplicateNames 1.0)
  $fragmentedRow=@(
    [pscustomobject]@{score=1.0;section='联系人';line=[pscustomobject]@{text='凉雨';y=100;cy=109;h=18}},
    [pscustomobject]@{score=1.0;section='联系人';line=[pscustomobject]@{text='凉雨';y=106;cy=113;h=14}}
  )
  $collapsedFragments=@(Get-NearCandidateRows $fragmentedRow 1.0)
  $mixedSections=@(
    [pscustomobject]@{text='联系人';x=0;y=10;w=40;h=14;cx=20;cy=17},
    [pscustomobject]@{text='凉雨';x=60;y=36;w=40;h=18;cx=80;cy=45},
    [pscustomobject]@{text='搜索网络结果';x=0;y=62;w=90;h=14;cx=45;cy=69},
    [pscustomobject]@{text='凉雨';x=60;y=78;w=40;h=18;cx=80;cy=87},
    [pscustomobject]@{text='群聊';x=0;y=90;w=40;h=14;cx=20;cy=97},
    [pscustomobject]@{text='凉雨工作群';x=60;y=120;w=90;h=18;cx=105;cy=129},
    [pscustomobject]@{text='凉雨工作群2';x=60;y=180;w=100;h=18;cx=110;cy=189}
  )
  $mixedSelection=Select-UniqueTarget $mixedSections '凉雨'
  $ok=$fixtureText.Contains('123') -and $exact -ge 0.99 -and $different -lt 0.76 -and
    $distinctRows.Count -eq 2 -and $collapsedFragments.Count -eq 1 -and
    $mixedSelection.code -eq 'ok' -and
    $mixedSelection.best.section -eq (Normalize-Name '联系人')
  Write-Result 'self_test' $ok '本地匹配逻辑、同名联系人防误发和分区优先级检查可用。' `
    ([pscustomobject]@{
      exact=$exact;different=$different;duplicateRows=$distinctRows.Count
      syntheticOcr=$fixtureText;elapsedMs=$helperTimer.ElapsedMilliseconds
      collapsedFragments=$collapsedFragments.Count
      mixedSection=$mixedSelection.best.section;mixedCandidateCount=@($mixedSelection.candidates).Count
    })
  exit $(if($ok){0}else{1})
}

$window=[IntPtr]::Zero
try{
  $window=[HuiTianWeixinNative]::FindMain()
  if($window -eq [IntPtr]::Zero){
    Write-Result 'not_ready' $false '没有找到已登录并运行的微信 4.x 主窗口。'
    exit 4
  }
  [HuiTianWeixinNative]::Activate($window)
  Trace-Stage 'window_activated'
  Start-Sleep -Milliseconds 300
  $rect=New-Object HuiTianWeixinNative+RECT
  [HuiTianWeixinNative]::GetWindowRect($window,[ref]$rect)|Out-Null
  $width=$rect.Right-$rect.Left;$height=$rect.Bottom-$rect.Top
  if($width -lt 600 -or $height -lt 400){
    Write-Result 'not_ready' $false '微信窗口尚未恢复到可操作大小。'
    exit 4
  }
  $dpiScale=1.0
  try{$dpi=[HuiTianWeixinNative]::GetDpiForWindow($window);if($dpi -gt 0){$dpiScale=$dpi/96.0}}catch{}
  function Px([double]$value){return [int][Math]::Round($value*$dpiScale)}

  # Already-open exact conversation is the cheapest path. Do not reopen the
  # search list, and never accept fuzzy/substring names for this shortcut.
  if(-not $DiagnosticOnly){
    $openTitleLines=Get-OcrLines ($rect.Left+(Px 300)) ($rect.Top+(Px 28)) (Px 430) (Px 48) 3.0
    $openTitle=(($openTitleLines|ForEach-Object{$_.text}) -join '')
    if((Normalize-Name $openTitle) -eq (Normalize-Name $Contact)){
      Trace-Stage 'existing_title_verified'
      Write-Result 'verified' $true '当前已打开的会话标题与目标完全一致，无需重新搜索。' `
        ([pscustomobject]@{title=$openTitle;score=1.0;handle=[int64]$window})
      exit 0
    }
  }

  # Weixin 4.x does not always honor Ctrl+F after a background window is
  # activated. Click the visible search field directly so the following text
  # cannot land in the conversation list or another window.
  [HuiTianWeixinNative]::ClickRestore(
    $rect.Left+(Px 160),
    $rect.Top+(Px 52))
  Start-Sleep -Milliseconds 180
  [HuiTianWeixinNative]::Ctrl(0x41) # Ctrl+A
  [System.Windows.Forms.Clipboard]::SetText($Contact,[System.Windows.Forms.TextDataFormat]::UnicodeText)
  [HuiTianWeixinNative]::Ctrl(0x56) # Ctrl+V
  Start-Sleep -Milliseconds 850
  Trace-Stage 'search_ready'

  # Include the section labels on the left. Starting at x=100 clipped most of
  # "联系人" on scaled displays, leaving the matcher unable to distinguish
  # the one contact row from lower group/chat-history rows.
  $resultsLeft=$rect.Left+(Px 58);$resultsTop=$rect.Top+(Px 70)
  $resultsWidth=Px 342
  $resultsHeight=[Math]::Min($height-(Px 98),(Px 680))
  $resultLines=Get-OcrLines $resultsLeft $resultsTop $resultsWidth $resultsHeight 2.35
  $selection=Select-UniqueTarget $resultLines $Contact
  if($selection.code -ne 'ok'){
    [HuiTianWeixinNative]::Press(0x1B)
    $candidateNames=@($selection.candidates|ForEach-Object{$_.line.text}|Select-Object -Unique)
    $detail=if($selection.code -eq 'ambiguous'){
      '搜索结果中有多个相近联系人，为避免发错人，本次没有发送。'
    }else{'没有在联系人或群聊结果中找到唯一、可信的目标，本次没有发送。'}
    $extra=[ordered]@{candidates=$candidateNames}
    if($DiagnosticOnly){
      # Diagnostic mode never opens a conversation or sends content. Keep the
      # OCR evidence in the one-shot JSON result so layout/section failures can
      # be fixed without retaining screenshots on disk.
      $extra['crop']=[pscustomobject]@{
        left=$resultsLeft;top=$resultsTop;width=$resultsWidth;height=$resultsHeight;dpiScale=$dpiScale
      }
      $extra['ocrLines']=@($resultLines|ForEach-Object{
        [pscustomobject]@{text=$_.text;x=$_.x;y=$_.y;w=$_.w;h=$_.h}
      })
    }
    Write-Result $selection.code $false $detail ([pscustomobject]$extra)
    exit 5
  }

  if($DiagnosticOnly){
    [HuiTianWeixinNative]::Press(0x1B)
    Write-Result 'candidate_verified' $true '已在最高优先级结果分区找到唯一可信候选；诊断模式未打开会话、未输入且未发送任何内容。' `
      ([pscustomobject]@{
        section=$selection.best.section;candidate=$selection.best.line.text
        candidateCount=@($selection.candidates).Count
      })
    exit 0
  }

  $target=$selection.best.line
  [HuiTianWeixinNative]::ClickRestore([int]$target.cx,[int]$target.cy)
  Start-Sleep -Milliseconds 720
  Trace-Stage 'conversation_opened'
  $titleLines=Get-OcrLines ($rect.Left+(Px 300)) ($rect.Top+(Px 28)) (Px 430) (Px 48) 3.0
  $title=(($titleLines|ForEach-Object{$_.text}) -join '')
  $titleScore=Get-MatchScore $Contact $title
  if($titleScore -lt 0.72){
    Write-Result 'title_mismatch' $false '已打开搜索结果，但聊天标题与目标不一致；为避免发错人，本次没有发送。' `
      ([pscustomobject]@{title=$title;score=$titleScore;candidate=$target.text})
    exit 6
  }

  Write-Result 'verified' $true '联系人搜索结果和当前聊天标题均已核对。' `
    ([pscustomobject]@{title=$title;score=$titleScore;candidate=$target.text;cx=$target.cx;cy=$target.cy;handle=[int64]$window})
  exit 0
} catch {
  Write-Result 'failed' $false ('微信快速核对没有完成：'+$_.Exception.Message)
  exit 7
} finally {
  if($window -ne [IntPtr]::Zero){[HuiTianWeixinNative]::Release($window)}
}
