namespace MonitorRFC.Domain;

public static class SourceCatalog
{
    public static readonly string[] Articles = ["69", "69-b", "69-b-bis", "49-bis"];
    private static Listing L(string slug, string name, string path, string table) => new("sat:69:" + slug, "69", slug, name, path, "dbo." + table);
    public static IReadOnlyList<Listing> Sat { get; } = [
        L("cancelados","Cancelados","Documents_AGR/Cancelados.csv","CanceladosDetalle"),
        L("reduccion-multas-74","Reducción de multas","Documents_AGR/ReduccionArt74CFF.csv","ReduccionMultasDetalle"),
        L("condonados-concurso-146b","Condonados de concurso mercantil","Cifras_SAT/Indef/Condonadosart146BCFF.csv","ConcursoMercantilDetalle"),
        L("reduccion-recargos-21","Reducción de recargos","Cifras_SAT/Indef/Condonadosart21CFF.csv","ReduccionRecargosDetalle"),
        L("condonados-decreto-2015","Condonados por decreto","Cifras_SAT/Indef/CondonadosporDecreto.csv","CondonadosDecretoDetalle"),
        L("condonados-2007-2015","Condonados del 01 de enero de 2007","Cifras_SAT/Indef/Condonados_07_15.csv","CondonadosHistoricoDetalle"),
        L("cancelados-146a-2007-2015","Cancelados Artículo 146A","Cifras_SAT/Indef/Cancelados_07_15.csv","CanceladosHistoricoDetalle"),
        L("retorno-inversiones","Retorno de inversiones","Cifras_SAT/Indef/Retornoinversiones.csv","RetornoInversionesDetalle"),
        L("exigibles","Exigibles","Documents_AGR/Exigibles.csv","ExigiblesDetalle"),
        L("firmes","Firmes","Documents_AGR/Firmes.csv","FirmesDetalle"),
        L("no-localizados","No localizados","Documents_AGR/No_localizados.csv","NoLocalizadosDetalle"),
        L("sentencias","Sentencias","Documents_AGR/Sentencias.csv","SentenciasDetalle"),
        L("csd-sin-efectos","Certificado de Sello Digital","Documents_AGR/CSDsinefectos.csv","CsdSinEfectosDetalle"),
        L("entes-publicos-omisos","Entes Públicos","Documents_AGR/EntespublicosydeGobiernoomisos.csv","EntesOmisosDetalle"),
        new("sat:69-b:listado-completo","69-b","listado-completo","Listado completo","Documents_AGAFF/Listado_completo_69-B.csv","dbo.Sat69BRegistro"),
        new("sat:69-b-bis:listado-completo","69-b-bis","listado-completo","Listado completo","Documents_AGGC/Listado_69_B_Bis_Completo.csv","dbo.Sat69BBisRegistro"),
        new("sat:69:documento-tecnico","69","documento-tecnico","Documento técnico y normativo","Cifras_SAT/Indef/causasymotivos.xls","dbo.DocumentoCelda",true)
    ];
    public static bool IsListing(string article, string slug) => Articles.Contains(article) &&
        (slug == "publicaciones-dof" || Sat.Any(x => x.Article == article && x.Slug == slug && !x.Document));
}

