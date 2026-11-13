using System.Globalization;

namespace MonitorRFC.Domain;

/// <summary>
/// Modelo de Artículo fiscal con identificador numérico oficial.
/// </summary>
public sealed record ArticuloCatalogo(
    int ArticuloId,
    string Clave,
    string Nombre,
    string Descripcion
);

/// <summary>
/// Modelo de Listado / Supuesto fiscal con identificador numérico oficial asociado a su Artículo.
/// </summary>
public sealed record ListadoCatalogo(
    int ListadoId,
    int ArticuloId,
    string ArticuloClave,
    string Clave,
    string Nombre,
    string Descripcion,
    string Fuente
);

/// <summary>
/// Modelo jerárquico que agrupa un Artículo y todos sus Listados disponibles.
/// </summary>
public sealed record ArticuloJerarquico(
    int ArticuloId,
    string Clave,
    string Nombre,
    string Descripcion,
    IReadOnlyList<ListadoCatalogo> Listados
);

/// <summary>
/// Representa un tipo fiscal numérico (1..N) para retrocompatibilidad con integraciones previas.
/// </summary>
public sealed record TipoArticulo(
    int TipoId,
    string Clave,
    string Articulo,
    string Listado,
    string Nombre,
    string Descripcion,
    string Fuente,
    string? SituacionFiltro = null
);

/// <summary>
/// Catálogo oficial indexado de Artículos (1..4) y Listados (1..24) del SAT y DOF.
/// </summary>
public static class CatalogoTipos
{
    // ========================================================================
    // 1. Catálogo Oficial de Artículos (IDs 1 al 4)
    // ========================================================================
    public static readonly IReadOnlyList<ArticuloCatalogo> Articulos =
    [
        new(1, "69",       "Artículo 69",     "Incumplidos, créditos fiscales firmes/exigibles, cancelaciones, condonaciones y no localizados."),
        new(2, "69-b",     "Artículo 69-B",   "Operaciones inexistentes / Emisores de comprobantes fiscales sin infraestructura (EFOS)."),
        new(3, "69-b-bis", "Artículo 69-B Bis","Transmisión indebida de pérdidas fiscales."),
        new(4, "49-bis",   "Artículo 49-Bis", "Presunción de falsedad de comprobantes fiscales de nómina y asimilados a salarios.")
    ];

    // ========================================================================
    // 2. Catálogo Oficial de Listados / Supuestos (IDs 1 al 24)
    // ========================================================================
    public static readonly IReadOnlyList<ListadoCatalogo> Listados =
    [
        // Art. 69 (ArticuloId: 1)
        new(1,  1, "69", "cancelados",                  "Cancelados",                            "Créditos fiscales cancelados por incosteabilidad o insolvencia.", "SAT"),
        new(2,  1, "69", "reduccion-multas-74",          "Reducción de multas",                   "Beneficiados con reducción de multas conforme al Art. 74 CFF.", "SAT"),
        new(3,  1, "69", "condonados-concurso-146b",     "Condonados de concurso mercantil",      "Condonación en términos del Art. 146-B CFF.", "SAT"),
        new(4,  1, "69", "reduccion-recargos-21",        "Reducción de recargos",                 "Beneficiados con reducción de recargos conforme al Art. 21 CFF.", "SAT"),
        new(5,  1, "69", "condonados-decreto-2015",      "Condonados por decreto",                "Condonaciones al amparo del decreto fiscal 2015.", "SAT"),
        new(6,  1, "69", "condonados-2007-2015",         "Condonados 2007 a 2015",                "Condonaciones acumuladas del 01 de enero de 2007 al 31 de diciembre de 2015.", "SAT"),
        new(7,  1, "69", "cancelados-146a-2007-2015",    "Cancelados Artículo 146-A",             "Cancelaciones acumuladas conforme al Art. 146-A CFF (2007-2015).", "SAT"),
        new(8,  1, "69", "retorno-inversiones",          "Retorno de inversiones",                "Contribuyentes adheridos al programa de retorno de inversiones.", "SAT"),
        new(9,  1, "69", "exigibles",                    "Exigibles",                             "Contribuyentes con créditos fiscales firmes y exigibles en cobro.", "SAT"),
        new(10, 1, "69", "firmes",                       "Firmes",                                "Contribuyentes con créditos fiscales determinados como firmes.", "SAT"),
        new(11, 1, "69", "no-localizados",               "No localizados",                        "Contribuyentes con estatus de no localizados en su domicilio fiscal.", "SAT"),
        new(12, 1, "69", "sentencias",                   "Sentencias",                            "Contribuyentes con sentencia condenatoria ejecutoriada por delito fiscal.", "SAT"),
        new(13, 1, "69", "csd-sin-efectos",              "CSD sin efectos",                       "Contribuyentes a quienes se dejó sin efectos su Certificado de Sello Digital.", "SAT"),
        new(14, 1, "69", "entes-publicos-omisos",        "Entes públicos omisos",                 "Entes públicos y dependencias de gobierno con omisiones fiscales.", "SAT"),
        new(18, 1, "69", "documento-tecnico",            "Documento técnico y normativo",         "Matriz técnica de causas y motivos de publicación del Art. 69.", "SAT"),

        // Art. 69-B (ArticuloId: 2)
        new(15, 2, "69-b", "definitivos",                "Definitivos",                           "Contribuyentes catalogados de manera definitiva como EFOS.", "SAT"),
        new(16, 2, "69-b", "desvirtuados",               "Desvirtuados",                          "Contribuyentes que aportaron pruebas y desvirtuaron la presunción de operaciones inexistentes.", "SAT"),
        new(17, 2, "69-b", "presuntos",                  "Presuntos",                             "Contribuyentes notificados bajo presunción de operaciones inexistentes.", "SAT"),
        new(19, 2, "69-b", "sentencias-favorables",      "Sentencias favorables",                 "Contribuyentes con resolución definitiva o sentencia favorable.", "SAT"),
        new(20, 2, "69-b", "listado-completo",           "Listado completo 69-B",                 "Relación consolidada general del SAT para el Art. 69-B.", "SAT"),
        new(21, 2, "69-b", "publicaciones-dof",          "Publicaciones DOF 69-B",                "Edictos, resoluciones y notificaciones publicadas en el DOF para Art. 69-B.", "DOF"),

        // Art. 69-B Bis (ArticuloId: 3)
        new(22, 3, "69-b-bis", "listado-completo",       "Listado completo 69-B Bis",             "Relación general de transmisión indebida de pérdidas fiscales.", "SAT"),
        new(23, 3, "69-b-bis", "publicaciones-dof",      "Publicaciones DOF 69-B Bis",            "Resoluciones y publicaciones en el DOF para Art. 69-B Bis.", "DOF"),

        // Art. 49-Bis (ArticuloId: 4)
        new(24, 4, "49-bis",   "publicaciones-dof",      "Publicaciones DOF 49-Bis",              "Publicaciones del DOF relativas a la fracción X del Art. 49 Bis CFF.", "DOF")
    ];

    // ========================================================================
    // 3. Catálogo Legacy de Tipos (1..22) para Retrocompatibilidad
    // ========================================================================
    public static readonly IReadOnlyList<TipoArticulo> Todos =
    [
        new(1,  "sat:69:cancelados",                  "69",       "cancelados",                  "Cancelados",                            "Contribuyentes con créditos fiscales cancelados por incosteabilidad o insolvencia.", "SAT"),
        new(2,  "sat:69:reduccion-multas-74",          "69",       "reduccion-multas-74",          "Reducción de multas",                   "Contribuyentes beneficiados con reducción de multas conforme al Art. 74 CFF.", "SAT"),
        new(3,  "sat:69:condonados-concurso-146b",     "69",       "condonados-concurso-146b",     "Condonados de concurso mercantil",      "Contribuyentes con condonación en términos del Art. 146-B CFF.", "SAT"),
        new(4,  "sat:69:reduccion-recargos-21",        "69",       "reduccion-recargos-21",        "Reducción de recargos",                 "Contribuyentes beneficiados con reducción de recargos conforme al Art. 21 CFF.", "SAT"),
        new(5,  "sat:69:condonados-decreto-2015",      "69",       "condonados-decreto-2015",      "Condonados por decreto",                "Condonaciones publicadas al amparo del decreto fiscal 2015.", "SAT"),
        new(6,  "sat:69:condonados-2007-2015",         "69",       "condonados-2007-2015",         "Condonados del 01 de enero de 2007",    "Condonaciones históricas acumuladas periodo 2007 a 2015.", "SAT"),
        new(7,  "sat:69:cancelados-146a-2007-2015",    "69",       "cancelados-146a-2007-2015",    "Cancelados Artículo 146A",              "Cancelaciones acumuladas conforme al Art. 146-A CFF (2007-2015).", "SAT"),
        new(8,  "sat:69:retorno-inversiones",          "69",       "retorno-inversiones",          "Retorno de inversiones",                "Contribuyentes adheridos al programa de retorno de inversiones en el extranjero.", "SAT"),
        new(9,  "sat:69:exigibles",                    "69",       "exigibles",                    "Exigibles",                             "Contribuyentes con créditos fiscales firmes y exigibles.", "SAT"),
        new(10, "sat:69:firmes",                       "69",       "firmes",                       "Firmes",                                "Contribuyentes con créditos fiscales determinados como firmes.", "SAT"),
        new(11, "sat:69:no-localizados",               "69",       "no-localizados",               "No localizados",                        "Contribuyentes con estatus de no localizados en su domicilio fiscal.", "SAT"),
        new(12, "sat:69:sentencias",                   "69",       "sentencias",                   "Sentencias",                            "Contribuyentes con sentencia condenatoria ejecutoriada por delito fiscal.", "SAT"),
        new(13, "sat:69:csd-sin-efectos",              "69",       "csd-sin-efectos",              "Certificado de Sello Digital",          "Contribuyentes a quienes se dejó sin efectos su CSD.", "SAT"),
        new(14, "sat:69:entes-publicos-omisos",        "69",       "entes-publicos-omisos",        "Entes Públicos",                        "Entes públicos y dependencias de gobierno omisos.", "SAT"),
        new(15, "sat:69-b:listado-completo",           "69-b",     "listado-completo",             "Listado completo 69-B",                 "Relación general completa de EFOS del Art. 69-B CFF.", "SAT"),
        new(16, "sat:69-b-bis:listado-completo",       "69-b-bis", "listado-completo",             "Listado completo 69-B Bis",             "Transmisión indebida de pérdidas fiscales (Art. 69-B Bis CFF).", "SAT"),
        new(17, "dof:69-b:publicaciones-dof",          "69-b",     "publicaciones-dof",            "Publicaciones DOF",                     "Notificaciones y resoluciones oficiales publicadas en el DOF para Art. 69-B.", "DOF"),
        new(18, "sat:69:documento-tecnico",            "69",       "documento-tecnico",            "Documento técnico y normativo",         "Matriz técnica y normativa de causas y motivos del Art. 69.", "SAT"),
        new(19, "sat:69-b:presuntos",                  "69-b",     "listado-completo",             "Art. 69-B Presuntos",                   "Contribuyentes notificados bajo presunción de operaciones inexistentes.", "SAT", "Presunto"),
        new(20, "sat:69-b:desvirtuados",               "69-b",     "listado-completo",             "Art. 69-B Desvirtuados",                "Contribuyentes que aportaron pruebas y desvirtuaron la presunción.", "SAT", "Desvirtuado"),
        new(21, "sat:69-b:definitivos",                "69-b",     "listado-completo",             "Art. 69-B Definitivos",                 "Contribuyentes catalogados de manera definitiva como EFOS.", "SAT", "Definitivo"),
        new(22, "sat:69-b:sentencias-favorables",      "69-b",     "listado-completo",             "Art. 69-B Sentencias favorables",       "Contribuyentes con sentencia favorable que anula la resolución definitiva.", "SAT", "Sentencia")
    ];

    private static readonly Dictionary<int, ArticuloCatalogo> ArticulosPorId = Articulos.ToDictionary(a => a.ArticuloId);
    private static readonly Dictionary<string, ArticuloCatalogo> ArticulosPorClave = Articulos.ToDictionary(a => a.Clave, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<int, ListadoCatalogo> ListadosPorId = Listados.ToDictionary(l => l.ListadoId);
    private static readonly Dictionary<string, TipoArticulo> TiposPorClave = Todos.ToDictionary(t => t.Clave, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, TipoArticulo> TiposPorId = Todos.ToDictionary(t => t.TipoId);

    // ========================================================================
    // 4. Métodos de Resolución y Normalización por Identificador o Clave
    // ========================================================================

    public static IReadOnlyList<ArticuloJerarquico> ObtenerJerarquiaCompleta() =>
        Articulos.Select(a => new ArticuloJerarquico(
            a.ArticuloId,
            a.Clave,
            a.Nombre,
            a.Descripcion,
            Listados.Where(l => l.ArticuloId == a.ArticuloId).ToArray()
        )).ToArray();

    public static bool NormalizarArticulo(string? input, out int articuloId, out string clave, out string nombre)
    {
        articuloId = 0; clave = ""; nombre = "";
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        if (int.TryParse(trimmed, out var id) && ArticulosPorId.TryGetValue(id, out var artById))
        {
            articuloId = artById.ArticuloId; clave = artById.Clave; nombre = artById.Nombre;
            return true;
        }

        var norm = TextKey.Normalize(trimmed).Replace("-", "").Replace("_", "").Replace(" ", "");
        var match = Articulos.FirstOrDefault(a =>
        {
            var aNorm = TextKey.Normalize(a.Clave).Replace("-", "").Replace("_", "").Replace(" ", "");
            return aNorm == norm || (norm == "49BIS" && a.Clave == "49-bis") || (norm == "69BBIS" && a.Clave == "69-b-bis") || (norm == "69B" && a.Clave == "69-b") || (norm == "69" && a.Clave == "69");
        });

        if (match is not null)
        {
            articuloId = match.ArticuloId; clave = match.Clave; nombre = match.Nombre;
            return true;
        }

        return false;
    }

    public static bool NormalizarListado(string? input, int? articuloId, out int listadoId, out string clave, out string nombre)
    {
        listadoId = 0; clave = ""; nombre = "";
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        if (int.TryParse(trimmed, out var id) && ListadosPorId.TryGetValue(id, out var listById))
        {
            listadoId = listById.ListadoId; clave = listById.Clave; nombre = listById.Nombre;
            return true;
        }

        var norm = TextKey.Normalize(trimmed).Replace("-", "").Replace("_", "").Replace(" ", "");
        var match = Listados.FirstOrDefault(l =>
        {
            if (articuloId.HasValue && articuloId.Value > 0 && l.ArticuloId != articuloId.Value) return false;
            var lNorm = TextKey.Normalize(l.Clave).Replace("-", "").Replace("_", "").Replace(" ", "");
            return lNorm == norm;
        });

        if (match is not null)
        {
            listadoId = match.ListadoId; clave = match.Clave; nombre = match.Nombre;
            return true;
        }

        return false;
    }

    public static int ObtenerArticuloId(string articulo) =>
        ArticulosPorClave.TryGetValue(articulo, out var a) ? a.ArticuloId : 1;

    public static string ObtenerArticuloNombre(string articulo) =>
        ArticulosPorClave.TryGetValue(articulo, out var a) ? a.Nombre : $"Artículo {articulo}";

    public static int ResolverListadoId(string articulo, string listado)
    {
        var match = Listados.FirstOrDefault(l =>
            string.Equals(l.ArticuloClave, articulo, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.Clave, listado, StringComparison.OrdinalIgnoreCase));
        return match?.ListadoId ?? 1;
    }

    public static string ObtenerListadoNombre(string articulo, string listado)
    {
        var match = Listados.FirstOrDefault(l =>
            string.Equals(l.ArticuloClave, articulo, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.Clave, listado, StringComparison.OrdinalIgnoreCase));
        return match?.Nombre ?? listado;
    }

    // Métodos legacy para mantener retrocompatibilidad total
    public static TipoArticulo? ObtenerPorId(int tipoId) => TiposPorId.GetValueOrDefault(tipoId);
    public static TipoArticulo? ObtenerPorClave(string clave) => TiposPorClave.GetValueOrDefault(clave);
    public static int ResolverTipoId(string articulo, string listado, string? situacion = null)
    {
        if (articulo == "69-b" && !string.IsNullOrWhiteSpace(situacion))
        {
            var norm = TextKey.Normalize(situacion);
            if (norm.Contains("DEFINITIVO")) return 21;
            if (norm.Contains("PRESUNTO")) return 19;
            if (norm.Contains("DESVIRTUADO")) return 20;
            if (norm.Contains("SENTENCIA")) return 22;
        }

        var match = Todos.FirstOrDefault(t =>
            string.Equals(t.Articulo, articulo, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Listado, listado, StringComparison.OrdinalIgnoreCase) &&
            t.SituacionFiltro is null);

        return match?.TipoId ?? 0;
    }
}
