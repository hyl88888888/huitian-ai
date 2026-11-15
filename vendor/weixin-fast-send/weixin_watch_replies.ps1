<#
  Persistent local reply watcher for Weixin 4.x GPU-rendered chats.
  It initializes Windows OCR once, establishes a baseline from the currently
  open conversation, then emits one compact JSON line for each stable new
  incoming message. No screenshot or recognized text is written to disk.
#>
param(
  [int]$LifetimeSeconds = 1900,
  [int]$PollMilliseconds = 650,
  [string]$ExpectedContact,
  [switch]$SelfTest
)

$ProgressPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
$VerbosePreference = 'SilentlyContinue'
$DebugPreference = 'SilentlyContinue'
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch {}

Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class HuiTianWeixinReplyNative {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr value);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);

  public static void EnableDpi(){
    try{ if(SetProcessDpiAwarenessContext(new IntPtr(-4))) return; }catch{}
    try{ SetProcessDpiAwareness(2); return; }catch{}
    try{ SetProcessDPIAware(); }catch{}
  }
  static bool IsQtWindowClass(string value){
    const string prefix="Qt", suffix="QWindowIcon";
    if(String.IsNullOrEmpty(value)||!value.StartsWith(prefix,StringComparison.Ordinal)||
       !value.EndsWith(suffix,StringComparison.Ordinal)) return false;
    int count=value.Length-prefix.Length-suffix.Length;
    if(count<=0) return false;
    for(int i=prefix.Length;i<prefix.Length+count;i++) if(!Char.IsDigit(value[i])) return false;
    return true;
  }
  public static IntPtr FindMain(){
    var found=new List<IntPtr>();
    EnumWindows((h,l)=>{
      uint pid; GetWindowThreadProcessId(h,out pid);
      try{
        var process=System.Diagnostics.Process.GetProcessById((int)pid);
        if(!String.Equals(process.ProcessName,"Weixin",StringComparison.OrdinalIgnoreCase)) return true;
      }catch{return true;}
      var className=new StringBuilder(256); GetClassName(h,className,256);
      if(IsQtWindowClass(className.ToString())) found.Add(h);
      return true;
    },IntPtr.Zero);
    IntPtr best=IntPtr.Zero; long area=-1; int titleScore=-1;
    foreach(var h in found){
      RECT r; GetWindowRect(h,out r); int w=r.Right-r.Left,hg=r.Bottom-r.Top;
      var title=new StringBuilder(256); GetWindowText(h,title,256);
      int score=title.ToString()=="微信"?1:0; long candidate=(long)Math.Max(0,w)*Math.Max(0,hg);
      if(!IsIconic(h)&&w>=700&&hg>=500&&(score>titleScore||(score==titleScore&&candidate>area))){
        best=h;area=candidate;titleScore=score;
      }
    }
    return best;
  }
}
'@ | Out-Null

try { [HuiTianWeixinReplyNative]::EnableDpi() } catch {}
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
  $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
  $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]
function Await($operation,$resultType){
  $task=$asTaskGeneric.MakeGenericMethod($resultType).Invoke($null,@($operation))
  $task.Wait(-1)|Out-Null
  return $task.Result
}
[Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine,Windows.Media,ContentType=WindowsRuntime] | Out-Null
[Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime] | Out-Null
$language=New-Object Windows.Globalization.Language('zh-Hans-CN')
$ocrEngine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language)
if($null -eq $ocrEngine){$ocrEngine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()}
if($null -eq $ocrEngine){exit 3}

function Get-BubbleColorEvidence([System.Drawing.Bitmap]$Bitmap,[int]$X,[int]$Y,[int]$Width,[int]$Height){
  # OCR bounds cover the glyphs, not the bubble. Sample a small padded area so
  # the fill colour wins over black text and colourful emoji pixels.
  if($null-eq$Bitmap-or$Width-le0-or$Height-le0){
    return [pscustomobject]@{bubbleClass='unknown';greenRatio=0.0;neutralRatio=0.0;samples=0}
  }
  $padX=[Math]::Max(8,[Math]::Min(18,[int]($Height*0.75)))
  $padY=[Math]::Max(4,[Math]::Min(10,[int]($Height*0.35)))
  $sampleLeft=[Math]::Max(0,$X-$padX);$sampleTop=[Math]::Max(0,$Y-$padY)
  $sampleRight=[Math]::Min($Bitmap.Width-1,$X+$Width+$padX)
  $sampleBottom=[Math]::Min($Bitmap.Height-1,$Y+$Height+$padY)
  if($sampleRight-lt$sampleLeft-or$sampleBottom-lt$sampleTop){
    return [pscustomobject]@{bubbleClass='unknown';greenRatio=0.0;neutralRatio=0.0;samples=0}
  }
  $area=($sampleRight-$sampleLeft+1)*($sampleBottom-$sampleTop+1)
  $step=[Math]::Max(1,[int][Math]::Ceiling([Math]::Sqrt($area/1600.0)))
  $sampleCount=0;$greenCount=0;$neutralCount=0
  for($py=$sampleTop;$py-le$sampleBottom;$py+=$step){
    for($px=$sampleLeft;$px-le$sampleRight;$px+=$step){
      $color=$Bitmap.GetPixel($px,$py);$r=[int]$color.R;$g=[int]$color.G;$b=[int]$color.B
      $sampleCount++
      # Covers current Weixin greens as well as the older #95EC69 palette,
      # while rejecting neutral grey/white chat backgrounds.
      if($g-ge120-and($g-$r)-ge18-and($g-$b)-ge18){$greenCount++;continue}
      # Incoming bubbles are a neutral light grey. Deliberately stop below
      # near-white so the pane background is not mistaken for a bubble.
      if($r-ge215-and$r-le247-and[Math]::Abs($r-$g)-le7-and[Math]::Abs($g-$b)-le7){$neutralCount++}
    }
  }
  if($sampleCount-eq0){
    return [pscustomobject]@{bubbleClass='unknown';greenRatio=0.0;neutralRatio=0.0;samples=0}
  }
  $greenRatio=$greenCount/[double]$sampleCount;$neutralRatio=$neutralCount/[double]$sampleCount
  $bubbleClass='unknown'
  if($greenCount-ge8-and$greenRatio-ge0.18){$bubbleClass='outgoing-green'}
  elseif($neutralCount-ge8-and$neutralRatio-ge0.18){$bubbleClass='incoming-neutral'}
  return [pscustomobject]@{
    bubbleClass=$bubbleClass
    greenRatio=[Math]::Round($greenRatio,3)
    neutralRatio=[Math]::Round($neutralRatio,3)
    samples=$sampleCount
  }
}

function Get-OcrLines([int]$Left,[int]$Top,[int]$Width,[int]$Height,[double]$Scale){
  $bitmap=$null;$graphics=$null;$large=$null;$largeGraphics=$null
  $memory=$null;$randomAccess=$null;$softwareBitmap=$null
  try{
    $bitmap=New-Object System.Drawing.Bitmap($Width,$Height)
    $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($Left,$Top,0,0,(New-Object System.Drawing.Size($Width,$Height)))
    $scaledWidth=[int]($Width*$Scale);$scaledHeight=[int]($Height*$Scale)
    $large=New-Object System.Drawing.Bitmap($scaledWidth,$scaledHeight)
    $largeGraphics=[System.Drawing.Graphics]::FromImage($large)
    $largeGraphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $largeGraphics.DrawImage($bitmap,0,0,$scaledWidth,$scaledHeight)
    $memory=New-Object System.IO.MemoryStream
    $large.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png)
    $memory.Position=0
    $randomAccess=[System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($memory)
    $decoder=Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($randomAccess)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $softwareBitmap=Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $result=Await ($ocrEngine.RecognizeAsync($softwareBitmap)) ([Windows.Media.Ocr.OcrResult])
    $lines=@()
    foreach($line in $result.Lines){
      $minX=1e9;$minY=1e9;$maxX=0;$maxY=0
      foreach($word in $line.Words){
        $b=$word.BoundingRect
        if($b.X-lt$minX){$minX=$b.X};if($b.Y-lt$minY){$minY=$b.Y}
        if($b.X+$b.Width-gt$maxX){$maxX=$b.X+$b.Width};if($b.Y+$b.Height-gt$maxY){$maxY=$b.Y+$b.Height}
      }
      if($minX-eq 1e9){continue}
      $localX=[int]($minX/$Scale);$localY=[int]($minY/$Scale)
      $w=[int](($maxX-$minX)/$Scale);$h=[int](($maxY-$minY)/$Scale)
      $x=$Left+$localX;$y=$Top+$localY
      $text=($line.Text -replace '\s+',' ').Trim()
      if($text){
        $colour=Get-BubbleColorEvidence $bitmap $localX $localY $w $h
        $lines += [pscustomobject]@{
          text=$text;x=[int]$x;y=[int]$y;w=$w;h=$h;cx=[int]($x+$w/2);bottom=[int]($y+$h)
          bubbleClass=$colour.bubbleClass;greenRatio=$colour.greenRatio;neutralRatio=$colour.neutralRatio
        }
      }
    }
    return @($lines)
  } finally {
    if($null-ne$softwareBitmap){$softwareBitmap.Dispose()};if($null-ne$randomAccess){$randomAccess.Dispose()}
    if($null-ne$memory){$memory.Dispose()};if($null-ne$largeGraphics){$largeGraphics.Dispose()}
    if($null-ne$large){$large.Dispose()};if($null-ne$graphics){$graphics.Dispose()};if($null-ne$bitmap){$bitmap.Dispose()}
  }
}

function Get-LatestIncoming($Lines,[int]$PaneLeft,[int]$PaneWidth,[double]$DpiScale){
  $paneMiddle=$PaneLeft+[int]($PaneWidth*0.50)
  $incomingAnchorLimit=$PaneLeft+[int]($PaneWidth*0.50)
  $filtered=@($Lines | Where-Object {
    # Colour is the strongest signal: an outgoing green bubble must never be
    # accepted even when a long line extends left of the pane midpoint. A
    # neutral bubble anchored on the left may itself cross the midpoint.
    $bubbleClass=[string]$_.bubbleClass
    $isIncomingByColour=$bubbleClass-eq'incoming-neutral'-and$_.x-lt$incomingAnchorLimit
    $isIncomingByPosition=$bubbleClass-ne'outgoing-green'-and$_.cx-lt$paneMiddle
    $bubbleClass-ne'outgoing-green'-and($isIncomingByColour-or$isIncomingByPosition)-and
    $_.text.Length -ge 1 -and $_.text.Length -le 500 -and
    $_.text -notmatch '^\d{1,2}:\d{2}$' -and
    $_.text -notmatch '^(今天|昨天|星期[一二三四五六日天]|周[一二三四五六日天])$'
  } | Sort-Object y)
  if($filtered.Count-eq 0){return $null}
  $groups=@();$current=@();$lastBottom=-100000;$gap=[Math]::Max(22,[int](34*$DpiScale))
  foreach($line in $filtered){
    if($current.Count-gt 0 -and $line.y-$lastBottom-gt$gap){$groups+=,@($current);$current=@()}
    $current+=,$line;$lastBottom=$line.bottom
  }
  if($current.Count-gt 0){$groups+=,@($current)}
  if($groups.Count-eq 0){return $null}
  $latest=$groups[$groups.Count-1]
  $text=(($latest|ForEach-Object{$_.text}) -join ' ').Trim()
  if(-not$text){return $null}
  return [pscustomobject]@{text=$text;y=($latest|Measure-Object y -Maximum).Maximum}
}

function Get-Fingerprint([string]$Text){
  $normalized=(($Text -replace '\s+','') -replace '[，。；：、,.!！?？（）()【】\[\]]','')
  $sha=[System.Security.Cryptography.SHA256]::Create()
  try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($normalized)))).Replace('-','')}
  finally{$sha.Dispose()}
}

function Normalize-ChatName([string]$Value){
  if($null-eq$Value){return ''}
  return (($Value -replace '\s','') -replace '[，。；：、,.!！?？（）()【】\[\]_-]','').ToLowerInvariant()
}

function Get-IdentityDisposition([string]$Visible,[string]$Bound,[bool]$Named,[int]$Failures){
  if($Visible-and$Bound-and(Normalize-ChatName $Visible)-eq(Normalize-ChatName $Bound)){
    return 'active'
  }
  # A named session may be temporarily hidden by another contact's turn.
  # Never emit that contact's message, but keep this watcher alive so it can
  # resume when the read-only navigator returns to its own conversation.
  if($Named){return 'paused'}
  # An explicit @current session has no stable name to navigate back to.
  # Switching away from it remains a terminal identity mismatch.
  if($Failures-ge2){return 'mismatch'}
  return 'retry'
}

if($SelfTest){
  $bitmap=$null;$graphics=$null;$font=$null;$textBrush=$null;$greyBrush=$null;$greenBrush=$null
  $memory=$null;$randomAccess=$null;$softwareBitmap=$null
  try{
    # Keep this entirely offline: draw representative Weixin bubbles in
    # memory, classify their fill colours, then OCR the same synthetic image.
    $bitmap=New-Object System.Drawing.Bitmap(800,260)
    $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::FromArgb(250,250,250))
    $greyBrush=New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(238,238,240))
    $greenBrush=New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(157,242,159))
    $textBrush=New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(22,22,22))
    $font=New-Object System.Drawing.Font('Microsoft YaHei',22)
    $graphics.FillRectangle($greyBrush,24,20,752,92)
    $graphics.FillRectangle($greenBrush,140,142,636,92)
    $graphics.DrawString('对方的长消息应当完整保留',$font,$textBrush,44,46)
    $graphics.DrawString('己方绿色消息必须排除',$font,$textBrush,164,168)

    $incomingColour=Get-BubbleColorEvidence $bitmap 42 42 710 42
    $outgoingColour=Get-BubbleColorEvidence $bitmap 162 164 590 42
    $syntheticLines=@(
      [pscustomobject]@{text='对方的长消息应当完整保留';x=24;y=45;w=752;h=32;cx=400;bottom=77;bubbleClass=$incomingColour.bubbleClass},
      # Its centre is left of the old 58% limit, reproducing the false
      # outgoing match shown in the captured conversation.
      [pscustomobject]@{text='己方绿色消息必须排除';x=140;y=168;w=636;h=32;cx=458;bottom=200;bubbleClass=$outgoingColour.bubbleClass}
    )
    $selected=Get-LatestIncoming $syntheticLines 0 800 1.0
    $incomingOk=$incomingColour.bubbleClass-eq'incoming-neutral'
    $outgoingOk=$outgoingColour.bubbleClass-eq'outgoing-green'
    $selectionOk=$null-ne$selected-and$selected.text-eq'对方的长消息应当完整保留'

    $memory=New-Object System.IO.MemoryStream;$bitmap.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png);$memory.Position=0
    $randomAccess=[System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($memory)
    $decoder=Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($randomAccess)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $softwareBitmap=Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $result=Await ($ocrEngine.RecognizeAsync($softwareBitmap)) ([Windows.Media.Ocr.OcrResult])
    $recognized=($result.Text -replace '\s+','').Trim();$ocrOk=$recognized.Length-gt2
    $namedPause=(Get-IdentityDisposition '小李' '小王' $true 2)-eq'paused'
    $namedResume=(Get-IdentityDisposition '小王' '小王' $true 2)-eq'active'
    $currentMismatch=(Get-IdentityDisposition '小李' '小王' $false 2)-eq'mismatch'
    $ok=$ocrOk-and$incomingOk-and$outgoingOk-and$selectionOk-and
      $namedPause-and$namedResume-and$currentMismatch
    [pscustomobject]@{
      ready=$true;selfTest=$true;ocrAvailable=$true;recognized=$recognized
      incomingBubble=$incomingColour.bubbleClass;outgoingBubble=$outgoingColour.bubbleClass
      outgoingExcluded=$selectionOk;longIncomingRetained=$selectionOk
      namedSwitchPauses=$namedPause;namedResumeWorks=$namedResume
      currentSwitchStops=$currentMismatch;ok=$ok
    } | ConvertTo-Json -Compress
    exit $(if($ok){0}else{4})
  } finally {
    if($null-ne$softwareBitmap){$softwareBitmap.Dispose()};if($null-ne$randomAccess){$randomAccess.Dispose()}
    if($null-ne$memory){$memory.Dispose()};if($null-ne$greenBrush){$greenBrush.Dispose()};if($null-ne$greyBrush){$greyBrush.Dispose()}
    if($null-ne$textBrush){$textBrush.Dispose()};if($null-ne$font){$font.Dispose()}
    if($null-ne$graphics){$graphics.Dispose()};if($null-ne$bitmap){$bitmap.Dispose()}
  }
}

$started=[DateTime]::UtcNow
$baseline='';$baselineInitialized=$false;$pending='';$pendingCount=0;$readySent=$false
$boundContact=if([string]::IsNullOrWhiteSpace($ExpectedContact)){''}else{$ExpectedContact.Trim()}
$namedContact=-not[string]::IsNullOrWhiteSpace($ExpectedContact)
$identityFailures=0
$contactActive=$false
while(([DateTime]::UtcNow-$started).TotalSeconds-lt[Math]::Max(30,$LifetimeSeconds)){
  try{
    $window=[HuiTianWeixinReplyNative]::FindMain()
    if($window-eq[IntPtr]::Zero){Start-Sleep -Milliseconds 900;continue}
    $rect=New-Object HuiTianWeixinReplyNative+RECT
    [HuiTianWeixinReplyNative]::GetWindowRect($window,[ref]$rect)|Out-Null
    $width=$rect.Right-$rect.Left;$height=$rect.Bottom-$rect.Top
    if($width-lt700-or$height-lt500){Start-Sleep -Milliseconds 900;continue}
    $dpiScale=1.0
    try{$dpi=[HuiTianWeixinReplyNative]::GetDpiForWindow($window);if($dpi-gt0){$dpiScale=$dpi/96.0}}catch{}
    # Bind every OCR read to the actual chat header. The main window can stay
    # open while a person (or another task) switches to a different contact.
    $titleLeft=$rect.Left+[int](300*$dpiScale)
    $titleTop=$rect.Top+[int](28*$dpiScale)
    $titleWidth=[Math]::Min([int](430*$dpiScale),$rect.Right-$titleLeft-8)
    $titleHeight=[Math]::Min([int](48*$dpiScale),$rect.Bottom-$titleTop-8)
    $visibleTitle=''
    if($titleWidth-gt40-and$titleHeight-gt20){
      $titleLines=Get-OcrLines $titleLeft $titleTop $titleWidth $titleHeight 3.0
      $visibleTitle=(($titleLines|ForEach-Object{$_.text}) -join '').Trim()
    }
    if(-not$boundContact-and$visibleTitle){$boundContact=$visibleTitle}
    $disposition=Get-IdentityDisposition $visibleTitle $boundContact $namedContact ($identityFailures+1)
    if($disposition-ne'active'){
      $identityFailures++
      if($disposition-eq'mismatch'){
        [pscustomobject]@{status='target_mismatch'} | ConvertTo-Json -Compress
        [Console]::Out.Flush()
        break
      }
      if($disposition-eq'paused'){
        if($contactActive){
          [pscustomobject]@{status='inactive'} | ConvertTo-Json -Compress
          [Console]::Out.Flush()
        }
        $contactActive=$false;$pending='';$pendingCount=0
      }
      Start-Sleep -Milliseconds ([Math]::Max(300,$PollMilliseconds))
      continue
    }
    $identityFailures=0
    if(-not$contactActive-and$readySent){
      [pscustomobject]@{status='active'} | ConvertTo-Json -Compress
      [Console]::Out.Flush()
    }
    $contactActive=$true
    $paneLeft=$rect.Left+[int](292*$dpiScale)
    $paneTop=$rect.Top+[int](78*$dpiScale)
    $paneWidth=[Math]::Max(300,$width-[int](315*$dpiScale))
    $paneHeight=[Math]::Max(180,$height-[int](330*$dpiScale))
    $lines=Get-OcrLines $paneLeft $paneTop $paneWidth $paneHeight 1.55
    $latest=Get-LatestIncoming $lines $paneLeft $paneWidth $dpiScale
    # A chat can switch while the larger message pane is being recognized.
    # Check the header again before treating its text as this contact's reply.
    $titleAfterLines=Get-OcrLines $titleLeft $titleTop $titleWidth $titleHeight 3.0
    $titleAfter=(($titleAfterLines|ForEach-Object{$_.text}) -join '').Trim()
    $afterDisposition=Get-IdentityDisposition $titleAfter $boundContact $namedContact ($identityFailures+1)
    if($afterDisposition-ne'active'){
      $identityFailures++
      if($afterDisposition-eq'mismatch'){
        [pscustomobject]@{status='target_mismatch'} | ConvertTo-Json -Compress
        [Console]::Out.Flush()
        break
      }
      if($afterDisposition-eq'paused'){
        if($contactActive){
          [pscustomobject]@{status='inactive'} | ConvertTo-Json -Compress
          [Console]::Out.Flush()
        }
        $contactActive=$false;$pending='';$pendingCount=0
      }
      Start-Sleep -Milliseconds ([Math]::Max(300,$PollMilliseconds))
      continue
    }
    $identityFailures=0
    if($null-ne$latest){
      $fingerprint=Get-Fingerprint $latest.text
      if(-not$baselineInitialized){$baseline=$fingerprint;$baselineInitialized=$true;$pending='';$pendingCount=0}
      elseif($fingerprint-ne$baseline){
        if($fingerprint-eq$pending){$pendingCount++}else{$pending=$fingerprint;$pendingCount=1}
        if($pendingCount-ge2){
          [pscustomobject]@{text=$latest.text;fingerprint=$fingerprint;at=[DateTimeOffset]::Now.ToString('O')} | ConvertTo-Json -Compress
          [Console]::Out.Flush();$baseline=$fingerprint;$pending='';$pendingCount=0
        }
      }else{$pending='';$pendingCount=0}
    }
    elseif(-not$baselineInitialized){$baseline='';$baselineInitialized=$true}
    if(-not$readySent){
      [pscustomobject]@{ready=$true;title=$boundContact;at=[DateTimeOffset]::Now.ToString('O')} | ConvertTo-Json -Compress
      [Console]::Out.Flush();$readySent=$true
    }
  } catch {
    # A transient redraw, resize, or covered window is normal. Keep watching;
    # never print raw OCR/error details to the product protocol.
  }
  Start-Sleep -Milliseconds ([Math]::Max(300,$PollMilliseconds))
}
