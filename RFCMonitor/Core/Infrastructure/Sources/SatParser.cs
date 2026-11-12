using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ExcelDataReader;
using Microsoft.VisualBasic.FileIO;
using MonitorRFC.Domain;

namespace MonitorRFC.Infrastructure.Sources;

public static class SatParser
{
    static SatParser()=>Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    private static Encoding DetectEncoding(string path)
    {
        using var stream=File.OpenRead(path);
        var bytes=new byte[65536];
        var n=stream.Read(bytes);
        if(n>=3&&bytes[0]==239&&bytes[1]==187&&bytes[2]==191) return new UTF8Encoding(true,true);
        try
        {
            var decoder=new UTF8Encoding(false,true).GetDecoder();
            do { decoder.GetCharCount(bytes,0,n,false); } while ((n=stream.Read(bytes)) > 0);
            decoder.GetCharCount([],0,0,true);
            return new UTF8Encoding(false,true);
        }
        catch(DecoderFallbackException) { return Encoding.GetEncoding(1252); }
    }
    public static DateOnly? ParseDate(string? value)
    {
        if(string.IsNullOrWhiteSpace(value)) return null;
        foreach(var culture in new[]{CultureInfo.GetCultureInfo("es-MX"),CultureInfo.InvariantCulture})
            if(DateOnly.TryParseExact(value.Trim(),["dd/MM/yyyy","d/M/yyyy","yyyy-MM-dd","dd-MM-yyyy","d-M-yyyy","dd.MM.yyyy","d 'de' MMMM 'de' yyyy","dd 'de' MMMM 'de' yyyy"],culture,DateTimeStyles.AllowWhiteSpaces,out var date)) return date;
        return null;
    }
    public static async IAsyncEnumerable<SourceRow> ReadAsync(Listing listing,string path,[EnumeratorCancellation] CancellationToken ct=default)
    {
        using var parser=new TextFieldParser(path,DetectEncoding(path),true) {TextFieldType=FieldType.Delimited,HasFieldsEnclosedInQuotes=true,TrimWhiteSpace=false};
        parser.SetDelimiters(",");
        string[]? headers=null;
        var physicalRow=0;
        for(var i=0;i<40&&!parser.EndOfData;i++)
        {
            var fields=parser.ReadFields()!;
            physicalRow++;
            if(fields.Any(x=>TextKey.Normalize(x)=="RFC")) {headers=fields;break;}
        }
        if(headers is null) throw new InvalidDataException("No se encontró cabecera RFC.");
        var headerRow=physicalRow;
        var keys=headers.Select(TextKey.Normalize).ToArray();
        var rfcIndex=Array.FindIndex(keys,k=>k is "RFC" or "REGISTROFEDERALDECONTRIBUYENTES" || k.Contains("RFC"));
        if(rfcIndex<0) throw new InvalidDataException("No se encontró cabecera RFC.");
        var nameIndex=Array.FindIndex(keys,k=>k.Contains("RAZONSOCIAL")||k is "CONTRIBUYENTE" or "NOMBREDELCONTRIBUYENTE" || k.Contains("NOMBRE"));
        if(nameIndex<0) throw new InvalidDataException("No se encontró columna de contribuyente.");
        var rowsRead=0;
        while(!parser.EndOfData)
        {
            ct.ThrowIfCancellationRequested();
            var fields=parser.ReadFields()!;
            physicalRow++;
            if(fields.All(string.IsNullOrWhiteSpace)) continue;
            if(fields.Length!=headers.Length)
            {
                if(fields.Length<headers.Length)
                {
                    var padded=new string[headers.Length];
                    Array.Copy(fields,padded,fields.Length);
                    for(int p=fields.Length;p<headers.Length;p++) padded[p]="";
                    fields=padded;
                }
            }
            rowsRead++;
            var rawVal=rfcIndex<fields.Length ? fields[rfcIndex] : "";
            var rfc=Rfc.Normalize(rawVal);
            if(string.IsNullOrWhiteSpace(rfc)) rfc=!string.IsNullOrWhiteSpace(rawVal) ? rawVal.Trim() : "(SIN RFC)";
            string? Get(params string[] names)
            {
                var i=Array.FindIndex(keys,k=>names.Contains(k));
                return i>=0 && i<fields.Length ? fields[i].Trim() : null;
            }
            var status=Get("SITUACIONDELCONTRIBUYENTE","SUPUESTO","SUPUESTODECANCELACIONCSD","MOTIVODECONDONACION","MOTIVO")??listing.Name;
            var dateText=Get("FECHADEPRIMERAPUBLICACION","FECHASDEPRIMERAPUBLICACION","FECHADEPUBLICACION");
            var typed=new Dictionary<string,object?>
            {
                ["tipoPersona"]=Get("TIPOPERSONA","TIPODEPERSONA"),["supuesto"]=status,
                ["entidadFederativa"]=Get("ENTIDADFEDERATIVA"),["fechaPrimeraPublicacion"]=ParseDate(dateText),
                ["fechaPublicacion"]=ParseDate(Get("FECHADEPUBLICACION")),
                ["fechaCancelacion"]=ParseDate(Get("FECHADECANCELACION","FECHADECANCELACIONCSD")),
                ["fechaAutorizacion"]=ParseDate(Get("FECHADEAUTORIZACION")),
                ["administracionResponsable"]=Get("ADMINISTRACIONGENERALRESPONSABLEDELACANCELACION"),
                ["ejercicio"]=Get("ANO","EJERCICIO"),["periodo"]=Get("PERIODO"),["motivo"]=Get("MOTIVO","MOTIVODECONDONACION"),
                ["numeroOriginal"]=Get("NO")
            };
            var amount=Get("MONTO","IMPORTE","IMPORTECONDONADO");
            if(decimal.TryParse(amount,NumberStyles.Currency,CultureInfo.GetCultureInfo("es-MX"),out var parsed)) typed["monto"]=parsed;
            else typed["monto"]=null;
            var transparency=Array.FindIndex(keys,k=>k.StartsWith("FECHADEPUBLICACIONCONMONTO"));
            if(transparency>=0 && transparency<fields.Length) typed["fechaPublicacionTransparencia"]=ParseDate(fields[transparency]);
            var stages=new List<object>();
            DateOnly? effectiveDate = ParseDate(dateText);
            if(listing.Article is "69-b" or "69-b-bis")
            {
                var names=listing.Article=="69-b" ? new[]{"presunto","desvirtuado","definitivo","sentencia-favorable"} : ["definitivo","sentencia-favorable"];
                var normStatus = TextKey.Normalize(status);
                DateOnly? stageMatchedDate = null;
                DateOnly? latestAnyStage = null;
                for(var j=0;j<names.Length;j++)
                {
                    var offset=4+j*4;
                    if(offset+3<fields.Length)
                    {
                        var fSat = ParseDate(fields[offset+1]);
                        var fDof = ParseDate(fields[offset+3]);
                        stages.Add(new {etapa=names[j],oficioSat=fields[offset],fechaSat=fSat,fechaSatOriginal=fields[offset+1],
                            oficioDof=fields[offset+2],fechaDof=fDof,fechaDofOriginal=fields[offset+3]});
                        var candidate = fDof ?? fSat;
                        if (candidate.HasValue && (latestAnyStage is null || candidate.Value > latestAnyStage.Value))
                            latestAnyStage = candidate;
                        var normEtapa = TextKey.Normalize(names[j]);
                        if (normStatus.Contains(normEtapa) || normEtapa.Contains(normStatus))
                        {
                            stageMatchedDate = candidate;
                        }
                    }
                }
                typed["etapas"]=stages;
                effectiveDate ??= stageMatchedDate ?? latestAnyStage;
            }
            var original=headers.Select((h,i)=>new OriginalCell(i+1,h,i<fields.Length?fields[i]:"")).ToArray();
            var nameVal=(nameIndex>=0 && nameIndex<fields.Length) ? fields[nameIndex].Trim() : "";
            yield return new(0,physicalRow,rfc,nameVal,listing.Article,listing.Slug,status,effectiveDate,
                Get("ENTIDADFEDERATIVA"),typed,original,headerRow);
            if(rowsRead%1000==0) await Task.Yield();
        }
        if(rowsRead==0) throw new InvalidDataException("CSV sin registros; se requiere verificar un vacío oficial.");
    }
    public static async IAsyncEnumerable<SourceRow> ReadWorkbookAsync(string path,[EnumeratorCancellation] CancellationToken ct=default)
    {
        using var stream=File.OpenRead(path);
        using var reader=ExcelReaderFactory.CreateReader(stream);
        var sheet=0;
        do
        {
            sheet++;
            var row=0;
            while(reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                row++;
                var cells=Enumerable.Range(0,reader.FieldCount).Select(i=>new OriginalCell(i+1,reader.Name,Convert.ToString(reader.GetValue(i),CultureInfo.InvariantCulture))).ToArray();
                yield return new(sheet,row,"","","69","documento-tecnico","",null,null,new(){["hoja"]=reader.Name},cells);
            }
            await Task.Yield();
        } while(reader.NextResult());
    }
}
