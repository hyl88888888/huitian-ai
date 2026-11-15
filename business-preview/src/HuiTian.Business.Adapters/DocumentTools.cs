using System.Text;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using HuiTian.Business.Core;
using A=DocumentFormat.OpenXml.Drawing;
using P=DocumentFormat.OpenXml.Presentation;
using S=DocumentFormat.OpenXml.Spreadsheet;
using W=DocumentFormat.OpenXml.Wordprocessing;

namespace HuiTian.Business.Adapters;

internal static class DocumentTools
{
    internal static ToolResult Create(BusinessPaths paths,JsonElement args)
    {
        string Get(string name)=>args.GetProperty(name).GetString()!;
        var kind=Get("kind");var title=Get("title");var content=Get("content");
        var file=FilePolicy.NewOutput(paths,Get("fileName"),"."+kind);
        var rows=args.GetProperty("rows").EnumerateArray().Select(r=>r.EnumerateArray().Select(c=>c.GetString()!).ToArray()).ToArray();
        var slides=args.GetProperty("slides").EnumerateArray().Select(s=>(Title:s.GetProperty("title").GetString()!,Body:s.GetProperty("body").GetString()!)).ToArray();
        if(kind=="xlsx" && rows.Length==0)throw new ArgumentException("电子表格至少需要一行rows。");
        if(kind=="pptx" && slides.Length==0)throw new ArgumentException("演示文稿至少需要一页slides。");
        if(rows.Any(r=>r.Any(c=>c.Length>32767)))throw new ArgumentException("单元格文本不能超过32767个字符。");
        if(rows.Sum(r=>r.Sum(c=>c.Length))>2_000_000)throw new ArgumentException("表格文本总量超过当前创建上限。");
        bool created=false;
        try
        {
            using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
            {
                created=true;
                switch(kind)
                {
                    case "txt":case "md":
                        using(var writer=new StreamWriter(output,new UTF8Encoding(false),leaveOpen:true))writer.Write((string.IsNullOrWhiteSpace(title)?"":(kind=="md"?"# ":"")+title+"\n\n")+content);
                        break;
                    case "docx":CreateWord(output,title,content);break;
                    case "xlsx":CreateWorkbook(output,rows);break;
                    case "pptx":CreatePresentation(output,slides);break;
                    default:throw new ArgumentException("不支持的文档类型。");
                }
            }
            var evidence=Verify(file);
            return DesktopToolRegistry.Ok($"已创建并重新打开验证：{Path.GetFileName(file)}。",evidence,[new(file,Path.GetFileName(file),kind)]);
        }
        catch {if(created)File.Delete(file);throw;}
    }
    private static void CreateWord(Stream output,string title,string content)
    {
        using var doc=WordprocessingDocument.Create(output,WordprocessingDocumentType.Document,true);
        var main=doc.AddMainDocumentPart();var body=new W.Body();
        if(!string.IsNullOrWhiteSpace(title))body.Append(new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines{After="280"}),new W.Run(new W.RunProperties(new W.Bold(),new W.FontSize{Val="36"}),new W.Text(title){Space=SpaceProcessingModeValues.Preserve})));
        foreach(var line in content.Replace("\r\n","\n").Split('\n'))body.Append(new W.Paragraph(new W.Run(new W.RunProperties(new W.RunFonts{Ascii="Aptos",HighAnsi="Aptos",EastAsia="Microsoft YaHei"},new W.FontSize{Val="22"}),new W.Text(line){Space=SpaceProcessingModeValues.Preserve})));
        body.Append(new W.SectionProperties(new W.PageSize{Width=11906,Height=16838},new W.PageMargin{Top=1440,Bottom=1440,Left=1440,Right=1440,Header=720,Footer=720,Gutter=0}));
        main.Document=new W.Document(body);main.Document.Save();
    }
    private static void CreateWorkbook(Stream output,string[][] rows)
    {
        using var doc=SpreadsheetDocument.Create(output,SpreadsheetDocumentType.Workbook,true);
        var main=doc.AddWorkbookPart();main.Workbook=new S.Workbook();var sheet=main.AddNewPart<WorksheetPart>();var data=new S.SheetData();
        for(int r=0;r<rows.Length;r++)
        {
            var row=new S.Row{RowIndex=(uint)r+1};
            for(int c=0;c<rows[r].Length;c++)row.Append(new S.Cell(new S.InlineString(new S.Text(rows[r][c]){Space=SpaceProcessingModeValues.Preserve})){CellReference=ColumnName(c+1)+(r+1),DataType=S.CellValues.InlineString});
            data.Append(row);
        }
        sheet.Worksheet=new S.Worksheet(data);main.Workbook.Append(new S.Sheets(new S.Sheet{Id=main.GetIdOfPart(sheet),SheetId=1,Name="Sheet1"}));
        sheet.Worksheet.Save();main.Workbook.Save();
    }
    private static P.ShapeTree ShapeTree()=>new(new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties{Id=1,Name=""},new P.NonVisualGroupShapeDrawingProperties(),new P.ApplicationNonVisualDrawingProperties()),new P.GroupShapeProperties(new A.TransformGroup(new A.Offset{X=0,Y=0},new A.Extents{Cx=0,Cy=0},new A.ChildOffset{X=0,Y=0},new A.ChildExtents{Cx=0,Cy=0})));
    private static void CreatePresentation(Stream output,(string Title,string Body)[] slides)
    {
        using var doc=PresentationDocument.Create(output,PresentationDocumentType.Presentation,true);
        var main=doc.AddPresentationPart();main.Presentation=new P.Presentation();
        var master=main.AddNewPart<SlideMasterPart>();var layout=master.AddNewPart<SlideLayoutPart>();
        var theme=master.AddNewPart<ThemePart>();theme.Theme=CreateTheme();theme.Theme.Save();
        layout.AddPart(master);
        layout.SlideLayout=new P.SlideLayout(new P.CommonSlideData(ShapeTree()),new P.ColorMapOverride(new A.MasterColorMapping())){Type=P.SlideLayoutValues.Blank,Preserve=true};layout.SlideLayout.Save();
        master.SlideMaster=new P.SlideMaster(new P.CommonSlideData(ShapeTree()),new P.ColorMap{Accent1=A.ColorSchemeIndexValues.Accent1,Accent2=A.ColorSchemeIndexValues.Accent2,Accent3=A.ColorSchemeIndexValues.Accent3,Accent4=A.ColorSchemeIndexValues.Accent4,Accent5=A.ColorSchemeIndexValues.Accent5,Accent6=A.ColorSchemeIndexValues.Accent6,Background1=A.ColorSchemeIndexValues.Light1,Background2=A.ColorSchemeIndexValues.Light2,Text1=A.ColorSchemeIndexValues.Dark1,Text2=A.ColorSchemeIndexValues.Dark2,Hyperlink=A.ColorSchemeIndexValues.Hyperlink,FollowedHyperlink=A.ColorSchemeIndexValues.FollowedHyperlink},new P.SlideLayoutIdList(new P.SlideLayoutId{Id=2147483649,RelationshipId=master.GetIdOfPart(layout)}),new P.TextStyles(new P.TitleStyle(),new P.BodyStyle(),new P.OtherStyle()));master.SlideMaster.Save();
        var ids=new P.SlideIdList();
        for(int i=0;i<slides.Length;i++)
        {
            var part=main.AddNewPart<SlidePart>();part.AddPart(layout);var shapes=ShapeTree();
            shapes.Append(TextShape(2,"Title",slides[i].Title,600000,480000,10900000,1100000,3400,true));
            shapes.Append(TextShape(3,"Body",slides[i].Body,600000,1800000,10900000,4300000,2200,false));
            part.Slide=new P.Slide(new P.CommonSlideData(new P.Background(new P.BackgroundProperties(new A.SolidFill(new A.RgbColorModelHex{Val="FFFFFF"}),new A.EffectList())),shapes),new P.ColorMapOverride(new A.MasterColorMapping()));part.Slide.Save();
            ids.Append(new P.SlideId{Id=(uint)i+256,RelationshipId=main.GetIdOfPart(part)});
        }
        main.Presentation.Append(new P.SlideMasterIdList(new P.SlideMasterId{Id=2147483648,RelationshipId=main.GetIdOfPart(master)}),ids,new P.SlideSize{Cx=12192000,Cy=6858000,Type=P.SlideSizeValues.Screen16x9},new P.NotesSize{Cx=6858000,Cy=9144000});main.Presentation.Save();
    }
    private static P.Shape TextShape(uint id,string name,string text,long x,long y,long cx,long cy,int size,bool bold)
    {
        var body=new P.TextBody(new A.BodyProperties{Wrap=A.TextWrappingValues.Square},new A.ListStyle());
        foreach(var line in text.Replace("\r\n","\n").Split('\n'))body.Append(new A.Paragraph(new A.Run(new A.RunProperties(new A.SolidFill(new A.RgbColorModelHex{Val="17243A"}),new A.LatinFont{Typeface="Aptos"},new A.EastAsianFont{Typeface="Microsoft YaHei"}){Language="zh-CN",FontSize=size,Bold=bold},new A.Text(line))));
        return new P.Shape(new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties{Id=id,Name=name},new P.NonVisualShapeDrawingProperties{TextBox=true},new P.ApplicationNonVisualDrawingProperties()),new P.ShapeProperties(new A.Transform2D(new A.Offset{X=x,Y=y},new A.Extents{Cx=cx,Cy=cy}),new A.PresetGeometry(new A.AdjustValueList()){Preset=A.ShapeTypeValues.Rectangle},new A.NoFill()),body);
    }
    private static A.Theme CreateTheme()
    {
        var colors=new A.ColorScheme(new A.Dark1Color(new A.SystemColor{Val=A.SystemColorValues.WindowText,LastColor="000000"}),new A.Light1Color(new A.SystemColor{Val=A.SystemColorValues.Window,LastColor="FFFFFF"}),new A.Dark2Color(new A.RgbColorModelHex{Val="17243A"}),new A.Light2Color(new A.RgbColorModelHex{Val="F2F5FA"}),new A.Accent1Color(new A.RgbColorModelHex{Val="2563EB"}),new A.Accent2Color(new A.RgbColorModelHex{Val="14B8A6"}),new A.Accent3Color(new A.RgbColorModelHex{Val="F59E0B"}),new A.Accent4Color(new A.RgbColorModelHex{Val="8B5CF6"}),new A.Accent5Color(new A.RgbColorModelHex{Val="EC4899"}),new A.Accent6Color(new A.RgbColorModelHex{Val="64748B"}),new A.Hyperlink(new A.RgbColorModelHex{Val="2563EB"}),new A.FollowedHyperlinkColor(new A.RgbColorModelHex{Val="8B5CF6"})){Name="HuiTian"};
        var fonts=new A.FontScheme(new A.MajorFont(new A.LatinFont{Typeface="Aptos Display"},new A.EastAsianFont{Typeface="Microsoft YaHei"},new A.ComplexScriptFont{Typeface="Arial"}),new A.MinorFont(new A.LatinFont{Typeface="Aptos"},new A.EastAsianFont{Typeface="Microsoft YaHei"},new A.ComplexScriptFont{Typeface="Arial"})){Name="HuiTian"};
        var formats=new A.FormatScheme(new A.FillStyleList(Enumerable.Range(0,3).Select(_=>new A.SolidFill(new A.SchemeColor{Val=A.SchemeColorValues.PhColor}))),new A.LineStyleList(Enumerable.Range(0,3).Select(_=>new A.Outline(new A.SolidFill(new A.SchemeColor{Val=A.SchemeColorValues.PhColor}),new A.PresetDash{Val=A.PresetLineDashValues.Solid}){Width=12700})),new A.EffectStyleList(Enumerable.Range(0,3).Select(_=>new A.EffectStyle(new A.EffectList()))),new A.BackgroundFillStyleList(Enumerable.Range(0,3).Select(_=>new A.SolidFill(new A.SchemeColor{Val=A.SchemeColorValues.PhColor})))){Name="HuiTian"};
        return new A.Theme(new A.ThemeElements(colors,fonts,formats)){Name="HuiTian"};
    }
    internal static ToolResult StylePresentation(BusinessPaths paths,string source,string fileName,string background,string font,string fontColor)
    {
        var input=FilePolicy.ReadFile(paths,source);if(Path.GetExtension(input).ToLowerInvariant()!=".pptx")throw new ArgumentException("输入必须为PPTX。");
        if(!Regex.IsMatch(background,"^[0-9A-Fa-f]{6}$")||!Regex.IsMatch(fontColor,"^[0-9A-Fa-f]{6}$"))throw new ArgumentException("颜色必须为六位十六进制，例如FFFFFF。");
        if(string.IsNullOrWhiteSpace(font)||font.Length>100||font.Any(char.IsControl))throw new ArgumentException("字体名称无效。");
        var output=FilePolicy.NewOutput(paths,fileName,".pptx");bool created=false;int count=0;
        try
        {
            File.Copy(input,output,false);created=true;
            using(var doc=PresentationDocument.Open(output,true))
            {
                var main=doc.PresentationPart??throw new ArgumentException("演示文稿缺少主体。");
                foreach(var part in main.SlideParts)
                {
                    var slide=part.Slide??throw new ArgumentException("页面缺少内容。");
                    var common=slide.CommonSlideData??throw new ArgumentException("页面缺少内容。");
                    common.Background=new P.Background(new P.BackgroundProperties(new A.SolidFill(new A.RgbColorModelHex{Val=background.ToUpperInvariant()}),new A.EffectList()));
                    SetTextStyle(slide,font,fontColor);slide.Save();count++;
                }
                foreach(var master in main.SlideMasterParts)
                {
                    var masterRoot=master.SlideMaster??throw new ArgumentException("母版缺少内容。");
                    SetTextStyle(masterRoot,font,fontColor);masterRoot.Save();
                    foreach(var layout in master.SlideLayoutParts){var layoutRoot=layout.SlideLayout??throw new ArgumentException("版式缺少内容。");SetTextStyle(layoutRoot,font,fontColor);layoutRoot.Save();}
                }
            }
            var verification=Verify(output);
            return DesktopToolRegistry.Ok($"已在新副本修改{count}页背景及文字字体；原文件保留。",new{source=input,slidesChanged=count,fontFamily=font,backgroundColor=background,fontColor,verification},[new(output,fileName,"pptx")]);
        }
        catch{if(created)File.Delete(output);throw;}
    }
    private static void SetTextStyle(OpenXmlElement root,string font,string color)
    {
        foreach(var run in root.Descendants<A.Run>())run.RunProperties??=new A.RunProperties();
        foreach(var field in root.Descendants<A.Field>())field.RunProperties??=new A.RunProperties();
        foreach(var properties in root.Descendants().Where(e=>e is A.RunProperties or A.DefaultRunProperties or A.EndParagraphRunProperties).Cast<OpenXmlCompositeElement>().ToArray())
        {
            properties.RemoveAllChildren<A.NoFill>();properties.RemoveAllChildren<A.SolidFill>();properties.RemoveAllChildren<A.GradientFill>();properties.RemoveAllChildren<A.BlipFill>();properties.RemoveAllChildren<A.PatternFill>();properties.RemoveAllChildren<A.GroupFill>();
            properties.AddChild(new A.SolidFill(new A.RgbColorModelHex{Val=color.ToUpperInvariant()}),true);
            properties.RemoveAllChildren<A.LatinFont>();properties.RemoveAllChildren<A.EastAsianFont>();properties.RemoveAllChildren<A.ComplexScriptFont>();
            properties.AddChild(new A.LatinFont{Typeface=font},true);properties.AddChild(new A.EastAsianFont{Typeface=font},true);properties.AddChild(new A.ComplexScriptFont{Typeface=font},true);
        }
    }
    internal static ToolResult Deduplicate(BusinessPaths paths,string source,string fileName,string sheetName,string[] keyColumns)
    {
        var input=FilePolicy.ReadFile(paths,source);if(Path.GetExtension(input).ToLowerInvariant()!=".xlsx")throw new ArgumentException("输入必须为XLSX。");
        var keys=keyColumns.Select(c=>c.ToUpperInvariant()).Distinct().ToArray();
        if(keys.Length==0||keys.Any(c=>!Regex.IsMatch(c,"^[A-Z]{1,3}$")||ColumnNumber(c)>16384))throw new ArgumentException("去重列必须为A至XFD中的列字母。");
        var output=FilePolicy.NewOutput(paths,fileName,".xlsx");bool created=false;int removed=0,before=0,after=0;
        try
        {
            File.Copy(input,output,false);created=true;
            using(var doc=SpreadsheetDocument.Open(output,true))
            {
                var main=doc.WorkbookPart??throw new ArgumentException("工作簿缺少主体。");
                var workbook=main.Workbook??throw new ArgumentException("工作簿缺少主体。");
                if(workbook.DefinedNames is not null||main.WorksheetParts.Any(w=>w.Worksheet?.Descendants<S.CellFormula>().Any()!=false)||main.ExternalWorkbookParts.Any()||main.PivotTableCacheDefinitionParts.Any())throw new ArgumentException("此工作簿含公式、命名引用、外部引用或透视表；当前去重工具仅处理普通值表格。");
                var sheet=workbook.Sheets?.Elements<S.Sheet>().SingleOrDefault(s=>s.Name?.Value==sheetName)??throw new ArgumentException("找不到指定工作表。");
                var part=(WorksheetPart)main.GetPartById(sheet.Id!);
                var worksheet=part.Worksheet??throw new ArgumentException("工作表缺少内容。");
                if(part.TableDefinitionParts.Any()||part.DrawingsPart is not null||worksheet.Elements<S.MergeCells>().Any()||worksheet.Elements<S.ConditionalFormatting>().Any()||worksheet.Elements<S.DataValidations>().Any()||worksheet.Elements<S.Hyperlinks>().Any()||worksheet.Elements<S.AutoFilter>().Any()||worksheet.Elements<S.SheetProtection>().Any())throw new ArgumentException("工作表含表格、图形、合并单元格或受引用规则，当前去重仅支持普通值表格。");
                var data=worksheet.GetFirstChild<S.SheetData>()??throw new ArgumentException("工作表缺少单元格数据。");
                if(data.Elements<S.Row>().Take(20001).Count()>20000)throw new ArgumentException("去重上限为20000行，请先拆分表格。");
                var rows=data.Elements<S.Row>().ToArray();before=rows.Length;var seen=new HashSet<string>(StringComparer.Ordinal);uint index=1;
                foreach(var row in rows)
                {
                    if(index>1)
                    {
                        var map=row.Elements<S.Cell>().ToDictionary(c=>Regex.Replace(c.CellReference?.Value??"","[0-9]",""),c=>CellText(c,main),StringComparer.OrdinalIgnoreCase);
                        var key=JsonSerializer.Serialize(keys.Select(k=>map.GetValueOrDefault(k,"")).ToArray());
                        if(!seen.Add(key)){row.Remove();removed++;continue;}
                    }
                    row.RowIndex=index;
                    foreach(var cell in row.Elements<S.Cell>())cell.CellReference=Regex.Replace(cell.CellReference?.Value??"A1","[0-9]+$",index.ToString());
                    index++;
                }
                after=data.Elements<S.Row>().Count();worksheet.SheetDimension=null;worksheet.Save();
            }
            var verification=Verify(output);
            return DesktopToolRegistry.Ok($"已生成去重副本，删除{removed}条重复数据行；首行表头保留。",new{source=input,sheetName,keyColumns=keys,rowsBefore=before,rowsAfter=after,removed,verification},[new(output,fileName,"xlsx")]);
        }
        catch{if(created)File.Delete(output);throw;}
    }
    internal static string ExtractText(string file,int limit)
    {
        var text=new StringBuilder();void Add(string value){if(text.Length<limit)text.Append(value.AsSpan(0,Math.Min(value.Length,limit-text.Length)));}
        switch(Path.GetExtension(file).ToLowerInvariant())
        {
            case ".txt":case ".md":case ".csv":case ".tsv":
                using(var reader=new StreamReader(file)){var chars=new char[limit];var count=reader.ReadBlock(chars,0,limit);text.Append(chars,0,count);}break;
            case ".docx":
                using(var doc=WordprocessingDocument.Open(file,false))foreach(var p in doc.MainDocumentPart?.Document?.Body?.Descendants<W.Paragraph>()??[]){Add(p.InnerText+"\n");if(text.Length>=limit)break;}break;
            case ".xlsx":
                using(var doc=SpreadsheetDocument.Open(file,false))
                {
                    var main=doc.WorkbookPart??throw new ArgumentException("工作簿缺少主体。");
                    foreach(var sheet in main.Workbook?.Sheets?.Elements<S.Sheet>()??[])
                    {Add("["+sheet.Name+"]\n");var part=(WorksheetPart)main.GetPartById(sheet.Id!);foreach(var row in part.Worksheet?.Descendants<S.Row>()??[]){Add(string.Join("\t",row.Elements<S.Cell>().Select(c=>CellText(c,main)))+"\n");if(text.Length>=limit)break;}if(text.Length>=limit)break;}
                }break;
            case ".pptx":
                using(var doc=PresentationDocument.Open(file,false))
                {var main=doc.PresentationPart??throw new ArgumentException("演示文稿缺少主体。");int i=0;foreach(var id in main.Presentation?.SlideIdList?.Elements<P.SlideId>()??[]){var part=(SlidePart)main.GetPartById(id.RelationshipId!);Add($"[幻灯片{++i}]\n");foreach(var p in part.Slide?.Descendants<A.Paragraph>()??[])Add(p.InnerText+"\n");if(text.Length>=limit)break;}}break;
            default:throw new ArgumentException("不支持读取此格式。");
        }
        return text.ToString();
    }
    private static string CellText(S.Cell cell,WorkbookPart main)
    {
        if(cell.DataType?.Value==S.CellValues.InlineString)return cell.InlineString?.InnerText??"";
        if(cell.DataType?.Value==S.CellValues.SharedString && int.TryParse(cell.CellValue?.Text,out int index))return main.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().ElementAtOrDefault(index)?.InnerText??"";
        return cell.CellValue?.Text??"";
    }
    private static object Verify(string file)
    {
        var kind=Path.GetExtension(file).ToLowerInvariant();int count=0;string unit="characters";
        using(OpenXmlPackage? doc=kind switch{".docx"=>WordprocessingDocument.Open(file,false),".xlsx"=>SpreadsheetDocument.Open(file,false),".pptx"=>PresentationDocument.Open(file,false),_=>null})
        {
            if(doc is not null)
            {
                var errors=new OpenXmlValidator().Validate(doc).Take(5).ToArray();
                if(errors.Length>0)throw new InvalidOperationException("文档结构复查未通过："+string.Join("；",errors.Select(e=>e.Description)));
                switch(doc){case WordprocessingDocument word:count=word.MainDocumentPart?.Document?.Body?.Elements<W.Paragraph>().Count()??0;unit="paragraphs";break;case SpreadsheetDocument book:count=book.WorkbookPart?.Workbook?.Sheets?.Count()??0;unit="worksheets";break;case PresentationDocument deck:count=deck.PresentationPart?.Presentation?.SlideIdList?.Count()??0;unit="slides";break;}
            }
        }
        var preview=ExtractText(file,4000);if(kind is ".txt" or ".md")count=new FileInfo(file).Length>0?preview.Length:0;
        return new{path=file,bytes=new FileInfo(file).Length,reopened=true,structureValidated=kind is not (".txt" or ".md"),count,unit,preview,verificationScope="Package reopened and OOXML schema/content checked; no Office visual rendering performed"};
    }
    private static string ColumnName(int number){var value="";while(number>0){number--;value=(char)('A'+number%26)+value;number/=26;}return value;}
    private static int ColumnNumber(string letters){int value=0;foreach(var c in letters)value=value*26+c-'A'+1;return value;}
}
