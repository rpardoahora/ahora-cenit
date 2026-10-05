using System.Text.RegularExpressions;

namespace AhoraCenit.Api.Services;

/// <summary>Resultado de adaptar un compose al SQL Server común.</summary>
/// <param name="Compose">Compose resultante (igual que el original si no había nada que adaptar).</param>
/// <param name="DisabledServices">Servicios SQL comentados.</param>
/// <param name="RewrittenServices">Servicios cuyas cadenas de conexión se han redirigido al SQL común.</param>
/// <param name="Databases">Bases de datos (ya con el prefijo de la instancia) a las que apuntan las cadenas.</param>
/// <param name="Warnings">Referencias a los servicios SQL que no se han podido adaptar automáticamente.</param>
public sealed record SharedSqlRewriteResult(
    string Compose,
    IReadOnlyList<string> DisabledServices,
    IReadOnlyList<string> RewrittenServices,
    IReadOnlyList<string> Databases,
    IReadOnlyList<string> Warnings)
{
    public bool Changed => DisabledServices.Count > 0 || RewrittenServices.Count > 0;
}

/// <summary>
/// Adapta el compose de un producto al modo "motor SQL común": comenta los
/// servicios SQL Server (y sus volúmenes y dependencias) y reescribe las cadenas
/// de conexión que apuntaban a ellos para que usen el SQL Server de la plataforma,
/// con bases propias de la instancia y el login SQL del cliente.
///
/// Trabaja línea a línea (no deserializa el YAML) para conservar el formato y los
/// comentarios del original y dejar comentado, no borrado, lo que se desactiva.
/// </summary>
public static class SharedSqlComposeRewriter
{
    public const string Marker = "[ahora-cenit]";

    private static readonly Regex TopLevelKey = new(@"^(?<key>[A-Za-z0-9_.-]+)\s*:\s*(#.*)?$");
    private static readonly Regex MapKey = new(@"^\s*[""']?(?<key>[A-Za-z0-9_.-]+)[""']?\s*:(?<rest>.*)$");
    private static readonly Regex ListItem = new(@"^\s*-\s*[""']?(?<value>[^""'#]*?)[""']?\s*(#.*)?$");
    private static readonly Regex InlineList = new(@"^(?<head>\s*[A-Za-z_]+\s*:\s*)\[(?<items>[^\]]*)\]\s*(#.*)?$");
    private static readonly Regex ImageValue = new(@"^\s*image\s*:\s*[""']?(?<image>[^""'\s#]+)");
    private static readonly Regex SqlImage = new(@"mssql|sqlserver|sql-server|azure-sql-edge", RegexOptions.IgnoreCase);
    private static readonly Regex SqlEula = new(@"\bACCEPT_EULA\b");
    private static readonly Regex SqlServerEnv = new(@"\b(MSSQL_SA_PASSWORD|SA_PASSWORD|MSSQL_PID)\b");
    private static readonly Regex NamedVolume = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$");
    private static readonly Regex VolumeSource = new(@"^\s*source\s*:\s*[""']?(?<name>[^""'\s#]+)");

    // Claves de una cadena de conexión ADO.NET / SqlClient.
    private static readonly Regex ServerKey = new(@"(?<![A-Za-z_])(?<k>server|data source|address|addr|network address)\s*=\s*(?<v>[^;""'\r\n]*)", RegexOptions.IgnoreCase);
    private static readonly Regex DatabaseKey = new(@"(?<![A-Za-z_])(?<k>initial catalog|database)\s*=\s*(?<v>[^;""'\r\n]*)", RegexOptions.IgnoreCase);
    private static readonly Regex UserKey = new(@"(?<![A-Za-z_])(?<k>user id|userid|uid|user)\s*=\s*(?<v>[^;""'\r\n]*)", RegexOptions.IgnoreCase);
    private static readonly Regex PasswordKey = new(@"(?<![A-Za-z_])(?<k>password|pwd)\s*=\s*(?<v>[^;""'\r\n]*)", RegexOptions.IgnoreCase);
    private static readonly Regex IntegratedSecurityKey = new(@"(?<![A-Za-z_])(?<k>integrated security|trusted_connection)\s*=\s*(?<v>[^;""'\r\n]*)", RegexOptions.IgnoreCase);

    /// <summary>Prefijo de las bases de datos de una instancia: su subdominio con '_' en vez de '-'.</summary>
    public static string DatabasePrefix(string subdomain) => subdomain.Replace('-', '_');

    /// <param name="compose">Plantilla compose del producto (con sus ${VARIABLES} sin sustituir).</param>
    /// <param name="databasePrefix">Prefijo para las bases de la instancia (ver <see cref="DatabasePrefix"/>).</param>
    /// <param name="userValue">Valor para User ID (normalmente una ${VARIABLE} que rellena el portal).</param>
    /// <param name="passwordValue">Valor para Password (normalmente una ${VARIABLE} que rellena el portal).</param>
    /// <param name="host">Host del SQL Server común.</param>
    /// <param name="network">Red Docker externa en la que está el SQL Server común.</param>
    public static SharedSqlRewriteResult Rewrite(
        string compose, string databasePrefix, string userValue, string passwordValue, string host, string network)
    {
        var lines = compose.Replace("\r\n", "\n").Split('\n').ToList();
        var edits = new Edits(lines);
        var empty = new SharedSqlRewriteResult(compose, [], [], [], []);

        var servicesLine = FindTopLevel(lines, "services");
        if (servicesLine < 0)
        {
            return empty;
        }

        var services = ReadChildren(lines, servicesLine);
        var sqlServices = services.Where(IsSqlService).ToList();
        if (sqlServices.Count == 0)
        {
            return empty;
        }

        var sqlNames = sqlServices.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var otherServices = services.Where(s => !sqlNames.Contains(s.Name)).ToList();
        var databases = new List<string>();
        var rewritten = new List<string>();
        var warnings = new List<string>();

        // 1. Volúmenes con nombre que solo usaban los servicios SQL.
        var sqlVolumes = sqlServices.SelectMany(s => NamedVolumes(lines, s)).ToHashSet();
        sqlVolumes.ExceptWith(otherServices.SelectMany(s => NamedVolumes(lines, s)));
        var volumesLine = FindTopLevel(lines, "volumes");
        if (volumesLine >= 0 && sqlVolumes.Count > 0)
        {
            CommentEntries(lines, edits, ReadChildren(lines, volumesLine), volumesLine, e => sqlVolumes.Contains(e.Name));
        }

        foreach (var service in otherServices)
        {
            // 2. Dependencias de los servicios SQL.
            var dependsOn = FindKey(lines, service, "depends_on");
            var dependsOnEnd = dependsOn;
            if (dependsOn >= 0)
            {
                RemoveDependencies(lines, edits, dependsOn, sqlNames);
                dependsOnEnd = dependsOn + 1;
                while (dependsOnEnd < service.End && (IsBlankOrComment(lines[dependsOnEnd]) || Indent(lines[dependsOnEnd]) > service.ChildIndent))
                {
                    dependsOnEnd++;
                }
            }

            // 3. Cadenas de conexión que apuntaban a un servicio SQL.
            var serviceRewritten = false;
            for (var i = service.Start + 1; i < service.End; i++)
            {
                if (IsBlankOrComment(lines[i]) || edits.IsCommented(i) || (i >= dependsOn && i < dependsOnEnd))
                {
                    continue;
                }

                var rewrittenLine = RewriteConnectionString(lines[i], sqlNames, databasePrefix, userValue, passwordValue, host, databases);
                if (rewrittenLine is not null)
                {
                    edits.Insert(i, $"{Spaces(Indent(lines[i]))}# {Marker} original: {lines[i].Trim()}");
                    edits.Replace(i, rewrittenLine);
                    serviceRewritten = true;
                }
                else if (sqlNames.Any(n => Regex.IsMatch(lines[i], $@"(?<![A-Za-z0-9_.-]){Regex.Escape(n)}(?![A-Za-z0-9_.-])", RegexOptions.IgnoreCase)))
                {
                    warnings.Add($"{service.Name}: {lines[i].Trim()}");
                }
            }

            // 4. Los servicios que ahora usan el SQL común necesitan su red.
            if (serviceRewritten)
            {
                rewritten.Add(service.Name);
                AddServiceNetwork(lines, edits, service, network);
            }
        }

        // Comentar los servicios SQL (después del resto: lo que se añade al final del
        // servicio anterior tiene que quedar antes de este comentario).
        foreach (var service in sqlServices)
        {
            edits.Insert(service.Start, $"{Spaces(service.Indent)}# {Marker} Motor SQL común: servicio \"{service.Name}\" desactivado; la aplicación usa el SQL Server de la plataforma.");
            edits.Comment(service.Start, service.End);
        }

        // 5. Declarar la red externa del SQL común.
        if (rewritten.Count > 0)
        {
            DeclareExternalNetwork(lines, edits, network);
        }

        var summary = new List<string>
        {
            $"# {Marker} Compose adaptado al motor SQL común de la plataforma:",
            $"#   - Servicios SQL desactivados: {string.Join(", ", sqlServices.Select(s => s.Name))}"
        };
        if (rewritten.Count > 0)
        {
            summary.Add($"#   - Cadenas de conexión redirigidas a {host} en: {string.Join(", ", rewritten)}");
        }
        if (databases.Count > 0)
        {
            summary.Add($"#   - Bases de datos de esta instancia: {string.Join(", ", databases.Distinct())}");
        }
        edits.Insert(0, summary.ToArray());

        return new SharedSqlRewriteResult(edits.Apply(), sqlServices.Select(s => s.Name).ToList(), rewritten, databases.Distinct().ToList(), warnings);
    }

    private static bool IsSqlService(Entry service)
    {
        if (service.Lines.Any(l => ImageValue.Match(l) is { Success: true } m && SqlImage.IsMatch(m.Groups["image"].Value)))
        {
            return true;
        }

        var text = string.Join('\n', service.Lines.Where(l => !IsBlankOrComment(l)));
        return SqlEula.IsMatch(text) && SqlServerEnv.IsMatch(text);
    }

    /// <summary>
    /// Reescribe una línea con una cadena de conexión que apunta a uno de los
    /// servicios SQL. Devuelve null si la línea no tiene ninguna.
    /// </summary>
    private static string? RewriteConnectionString(
        string line, HashSet<string> sqlNames, string databasePrefix, string userValue, string passwordValue, string host, List<string> databases)
    {
        var server = ServerKey.Match(line);
        if (!server.Success || !sqlNames.Contains(ServerHost(server.Groups["v"].Value)))
        {
            return null;
        }

        var result = ServerKey.Replace(line, m => $"{m.Groups["k"].Value}={host}", 1);
        result = DatabaseKey.Replace(result, m =>
        {
            var name = $"{databasePrefix}_{m.Groups["v"].Value.Trim()}";
            databases.Add(name);
            return $"{m.Groups["k"].Value}={name}";
        }, 1);
        result = IntegratedSecurityKey.Replace(result, m => $"{m.Groups["k"].Value}=false", 1);

        var hasUser = UserKey.IsMatch(result);
        var hasPassword = PasswordKey.IsMatch(result);
        result = UserKey.Replace(result, m => $"{m.Groups["k"].Value}={userValue}", 1);
        result = PasswordKey.Replace(result, m => $"{m.Groups["k"].Value}={passwordValue}", 1);

        if (!hasUser || !hasPassword)
        {
            var extra = (hasUser ? "" : $";User ID={userValue}") + (hasPassword ? "" : $";Password={passwordValue}");
            var trimmed = result.TrimEnd();
            var quote = trimmed.Length > 0 && trimmed[^1] is '"' or '\'' ? trimmed[^1].ToString() : "";
            var body = trimmed[..^quote.Length].TrimEnd(';');
            result = body + extra + quote;
        }

        return result;
    }

    /// <summary>"tcp:db,1433", "db\INSTANCIA" o "db:1433" → "db".</summary>
    private static string ServerHost(string value)
    {
        var host = value.Trim();
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        var cut = host.IndexOfAny([',', '\\', ':']);
        return cut >= 0 ? host[..cut] : host;
    }

    private static void RemoveDependencies(List<string> lines, Edits edits, int keyLine, HashSet<string> sqlNames)
    {
        var inline = InlineList.Match(lines[keyLine]);
        if (inline.Success)
        {
            var kept = inline.Groups["items"].Value.Split(',')
                .Select(i => i.Trim().Trim('"', '\''))
                .Where(i => i.Length > 0 && !sqlNames.Contains(i))
                .ToList();
            if (kept.Count == 0)
            {
                edits.Comment(keyLine, keyLine + 1);
            }
            else
            {
                edits.Insert(keyLine, $"{Spaces(Indent(lines[keyLine]))}# {Marker} original: {lines[keyLine].Trim()}");
                edits.Replace(keyLine, $"{inline.Groups["head"].Value}[{string.Join(", ", kept)}]");
            }

            return;
        }

        var entries = ReadChildren(lines, keyLine);
        CommentEntries(lines, edits, entries, keyLine, e => sqlNames.Contains(e.Name));
    }

    /// <summary>Comenta las entradas que cumplan el filtro; si no queda ninguna activa, también la clave padre.</summary>
    private static void CommentEntries(List<string> lines, Edits edits, List<Entry> entries, int parentLine, Func<Entry, bool> filter)
    {
        var removed = entries.Where(filter).ToList();
        foreach (var entry in removed)
        {
            edits.Comment(entry.Start, entry.End);
        }

        if (removed.Count > 0 && removed.Count == entries.Count)
        {
            edits.Comment(parentLine, parentLine + 1);
        }
    }

    private static void AddServiceNetwork(List<string> lines, Edits edits, Entry service, string network)
    {
        var childIndent = Spaces(service.ChildIndent);
        var networksLine = FindKey(lines, service, "networks");
        if (networksLine < 0)
        {
            // Sin "networks" el servicio estaba solo en la red default: se mantiene.
            edits.Insert(service.End, $"{childIndent}networks:", $"{childIndent}  - default", $"{childIndent}  - {network}");
            return;
        }

        var inline = InlineList.Match(lines[networksLine]);
        if (inline.Success)
        {
            var items = inline.Groups["items"].Value.Split(',').Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
            if (!items.Contains(network))
            {
                items.Add(network);
                edits.Replace(networksLine, $"{inline.Groups["head"].Value}[{string.Join(", ", items)}]");
            }

            return;
        }

        var entries = ReadChildren(lines, networksLine);
        if (entries.Any(e => e.Name == network))
        {
            return;
        }

        var itemIndent = entries.Count > 0 ? Spaces(entries[0].Indent) : childIndent + "  ";
        var isList = entries.Count == 0 || lines[entries[0].Start].TrimStart().StartsWith('-');
        edits.Insert(networksLine + 1, isList ? $"{itemIndent}- {network}" : $"{itemIndent}{network}: {{}}");
    }

    private static void DeclareExternalNetwork(List<string> lines, Edits edits, string network)
    {
        var networksLine = FindTopLevel(lines, "networks");
        if (networksLine < 0)
        {
            edits.Append("networks:", $"  {network}:", "    external: true");
            return;
        }

        var entries = ReadChildren(lines, networksLine);
        if (entries.Any(e => e.Name == network))
        {
            return;
        }

        var indent = entries.Count > 0 ? entries[0].Indent : 2;
        var end = entries.Count > 0 ? entries[^1].End : networksLine + 1;
        edits.Insert(end, $"{Spaces(indent)}{network}:", $"{Spaces(indent + 2)}external: true");
    }

    private static IEnumerable<string> NamedVolumes(List<string> lines, Entry service)
    {
        var volumesLine = FindKey(lines, service, "volumes");
        if (volumesLine < 0)
        {
            yield break;
        }

        foreach (var entry in ReadChildren(lines, volumesLine))
        {
            foreach (var line in entry.Lines)
            {
                var source = VolumeSource.Match(line);
                if (source.Success)
                {
                    if (NamedVolume.IsMatch(source.Groups["name"].Value))
                    {
                        yield return source.Groups["name"].Value;
                    }

                    continue;
                }

                var item = ListItem.Match(line);
                if (item.Success)
                {
                    var name = item.Groups["value"].Value.Split(':')[0].Trim();
                    if (NamedVolume.IsMatch(name))
                    {
                        yield return name;
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------- lectura del YAML por indentación

    /// <summary>Un bloque: la línea de su clave (o elemento de lista) y sus líneas hijas, sin las líneas vacías del final.</summary>
    private sealed record Entry(string Name, int Start, int End, int Indent, int ChildIndent, IReadOnlyList<string> Lines);

    private static int FindTopLevel(List<string> lines, string key) =>
        lines.FindIndex(l => TopLevelKey.Match(l) is { Success: true } m && m.Groups["key"].Value == key);

    /// <summary>Clave directa de un servicio (p. ej. "networks"), o -1.</summary>
    private static int FindKey(List<string> lines, Entry service, string key)
    {
        for (var i = service.Start + 1; i < service.End; i++)
        {
            if (!IsBlankOrComment(lines[i]) && Indent(lines[i]) == service.ChildIndent
                && MapKey.Match(lines[i]) is { Success: true } m && m.Groups["key"].Value == key)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Hijos directos de la línea <paramref name="parent"/> (claves de un mapa o elementos de una lista).</summary>
    private static List<Entry> ReadChildren(List<string> lines, int parent)
    {
        var parentIndent = Indent(lines[parent]);
        var children = new List<Entry>();
        var childIndent = -1;
        var i = parent + 1;
        while (i < lines.Count)
        {
            if (IsBlankOrComment(lines[i]))
            {
                i++;
                continue;
            }

            var indent = Indent(lines[i]);
            if (indent <= parentIndent && !(indent == parentIndent && lines[i].TrimStart().StartsWith('-')))
            {
                break;
            }

            if (childIndent < 0)
            {
                childIndent = indent;
            }

            if (indent != childIndent)
            {
                i++;
                continue;
            }

            var start = i;
            var isListItem = lines[i].TrimStart().StartsWith('-');
            i++;
            while (i < lines.Count && (IsBlankOrComment(lines[i]) || Indent(lines[i]) > childIndent))
            {
                i++;
            }

            var contentEnd = i;
            while (contentEnd > start + 1 && IsBlankOrComment(lines[contentEnd - 1]))
            {
                contentEnd--;
            }

            var name = isListItem
                ? ListItem.Match(lines[start]).Groups["value"].Value.Split(':')[0].Trim()
                : MapKey.Match(lines[start]).Groups["key"].Value;
            var grandChild = Enumerable.Range(start + 1, contentEnd - start - 1)
                .Where(j => !IsBlankOrComment(lines[j]))
                .Select(j => Indent(lines[j]))
                .DefaultIfEmpty(childIndent + 2)
                .First();

            children.Add(new Entry(name, start, contentEnd, childIndent, grandChild, lines.GetRange(start, contentEnd - start)));
        }

        return children;
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    private static bool IsBlankOrComment(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith('#');
    }

    private static string Spaces(int count) => new(' ', count);

    /// <summary>Cambios pendientes sobre las líneas originales; se aplican de una vez al final.</summary>
    private sealed class Edits(List<string> lines)
    {
        private readonly HashSet<int> _commented = [];
        private readonly Dictionary<int, string> _replaced = [];
        private readonly Dictionary<int, List<string>> _inserted = [];
        private readonly List<string> _appended = [];

        public bool IsCommented(int line) => _commented.Contains(line);

        public void Comment(int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                if (!IsBlankOrComment(lines[i]))
                {
                    _commented.Add(i);
                }
            }
        }

        public void Replace(int line, string text) => _replaced[line] = text;

        public void Insert(int beforeLine, params string[] text)
        {
            if (!_inserted.TryGetValue(beforeLine, out var list))
            {
                _inserted[beforeLine] = list = [];
            }

            list.AddRange(text);
        }

        public void Append(params string[] text) => _appended.AddRange(text);

        public string Apply()
        {
            var output = new List<string>();
            for (var i = 0; i <= lines.Count; i++)
            {
                if (_inserted.TryGetValue(i, out var before))
                {
                    output.AddRange(before);
                }

                if (i == lines.Count)
                {
                    break;
                }

                var line = _replaced.TryGetValue(i, out var replaced) ? replaced : lines[i];
                if (_commented.Contains(i))
                {
                    var indent = Indent(line);
                    line = $"{Spaces(indent)}# {line[indent..]}";
                }

                output.Add(line);
            }

            if (_appended.Count > 0)
            {
                while (output.Count > 0 && output[^1].Trim().Length == 0)
                {
                    output.RemoveAt(output.Count - 1);
                }

                output.AddRange(_appended);
                output.Add(string.Empty);
            }

            return string.Join('\n', output);
        }
    }
}
