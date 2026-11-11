namespace MonitorRFC.Domain;

public static class SourceCatalog
{
    public static readonly string[] Articles = ["69", "69-b", "69-b-bis", "49-bis"];
    private static Listing L(string slug, string name, string path) => new("sat:69:" + slug, "69", slug, name, path);
    public static IReadOnlyList<Listing> Sat { get; } = [
        L("cancelados","Cancelados","Documents_AGR/Cancelados.csv"),
        L("reduccion-multas-74","Reducción de multas","Documents_AGR/ReduccionArt74CFF.csv"),
        L("condonados-concurso-146b","Condonados de concurso mercantil","Cifras_SAT/Indef/Condonadosart146BCFF.csv"),
        L("reduccion-recargos-21","Reducción de recargos","Cifras_SAT/Indef/Condonadosart21CFF.csv"),
        L("condonados-decreto-2015","Condonados por decreto","Cifras_SAT/Indef/CondonadosporDecreto.csv"),
        L("condonados-2007-2015","Condonados del 01 de enero de 2007","Cifras_SAT/Indef/Condonados_07_15.csv"),
        L("cancelados-146a-2007-2015","Cancelados Artículo 146A","Cifras_SAT/Indef/Cancelados_07_15.csv"),
        L("retorno-inversiones","Retorno de inversiones","Cifras_SAT/Indef/Retornoinversiones.csv"),
        L("exigibles","Exigibles","Documents_AGR/Exigibles.csv"), L("firmes","Firmes","Documents_AGR/Firmes.csv"),
        L("no-localizados","No localizados","Documents_AGR/No_localizados.csv"), L("sentencias","Sentencias","Documents_AGR/Sentencias.csv"),
        L("csd-sin-efectos","Certificado de Sello Digital","Documents_AGR/CSDsinefectos.csv"), L("entes-publicos-omisos","Entes Públicos","Documents_AGR/EntespublicosydeGobiernoomisos.csv"),
        new("sat:69-b:listado-completo","69-b","listado-completo","Listado completo","Documents_AGAFF/Listado_completo_69-B.csv"),
        new("sat:69-b-bis:listado-completo","69-b-bis","listado-completo","Listado completo","Documents_AGGC/Listado_69_B_Bis_Completo.csv"),
        new("sat:69:documento-tecnico","69","documento-tecnico","Documento técnico y normativo","Cifras_SAT/Indef/causasymotivos.xls",true)
    ];
    public static bool IsListing(string article, string slug) => Articles.Contains(article) &&
        (slug == "publicaciones-dof" || Sat.Any(x => x.Article == article && x.Slug == slug && !x.Document));
}
