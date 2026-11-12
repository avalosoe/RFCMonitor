namespace MonitorRFC.Domain;

/// <summary>
/// Representa un tipo fiscal numérico (1..N) asignado a un artículo y listado oficial.
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
/// Catálogo oficial indexado de tipos de listados fiscales (1 al N) para Artículos 69, 69-B, 69-B Bis y DOF.
/// </summary>
public static class CatalogoTipos
{
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

    private static readonly Dictionary<int, TipoArticulo> PorId = Todos.ToDictionary(t => t.TipoId);
    private static readonly Dictionary<string, TipoArticulo> PorClave = Todos.ToDictionary(t => t.Clave, StringComparer.OrdinalIgnoreCase);

    public static TipoArticulo? ObtenerPorId(int tipoId) => PorId.GetValueOrDefault(tipoId);

    public static TipoArticulo? ObtenerPorClave(string clave) => PorClave.GetValueOrDefault(clave);

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
