using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Crayon;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.XEvent.XELite;

namespace XELive
{
    class XEStreamer
    {
        Profile _profile;
        HashSet<string> _onlyDatabases, _onlyUsers, _onlyStatements, _onlyPrefixes;
        HashSet<string> _ignoredDatabases, _ignoredUsers, _ignoredStatements, _ignoredPrefixes;
        Dictionary<long, IXEvent> _transactions;

        public XEStreamer(Profile profile)
        {
            _profile = profile;

            _onlyDatabases = CreateFilterSet(profile.OnlyDatabases);
            _onlyUsers = CreateFilterSet(profile.OnlyUsers);
            _onlyStatements = CreateFilterSet(profile.OnlyStatements);
            _onlyPrefixes = CreateFilterSet(profile.OnlyPrefixes);

            _ignoredDatabases = CreateFilterSet(profile.IgnoreDatabases);
            _ignoredUsers = CreateFilterSet(profile.IgnoreUsers);
            _ignoredStatements = CreateFilterSet(profile.IgnoreStatements);
            _ignoredPrefixes = CreateFilterSet(profile.IgnorePrefixes);

            _transactions = new();
        }

        private static HashSet<string> CreateFilterSet(IEnumerable<string> elements)
        {
            var set = new HashSet<string>(elements, StringComparer.OrdinalIgnoreCase);
            return set.Count == 0 ? null : set;
        }

        private static bool? IsPrefixInSet(HashSet<string> prefixSet, string statement)
        {
            if (prefixSet == null || statement == null)
                return null;

            foreach (var prefix in prefixSet)
            {
                if (statement.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public async Task Run(string query, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Connecting to {Output.Green(_profile.ConnectionString)}");
            using (var sql = new SqlConnection(_profile.ConnectionString))
            {
                await sql.OpenAsync();

                // drop old sessions
                var oldSessions = new List<string>();
                await sql.ExecuteReaderAsync($"SELECT [name] FROM [master].[sys].[server_event_sessions] WHERE [name] LIKE @pattern",
                    new { pattern = $"xelive-{_profile.Name}-%" },
                    h => oldSessions.Add(h.GetString(0)));

                foreach (var oldName in oldSessions)
                {
                    Console.WriteLine(Output.Yellow($"Dropping abandoned session {Output.Bold(oldName)}"));
                    await sql.ExecuteAsync($"DROP EVENT SESSION [{oldName}] ON SERVER");
                }

                var id = $"xelive-{_profile.Name}-{Guid.NewGuid()}";

                IEnumerable<string> ActionList()
                {
                    yield return "nt_username";
                    yield return "client_app_name";
                    yield return "server_principal_name";
                    yield return "database_name";
                    yield return "session_server_principal_name";

                    if (_profile.ShowTransactionIds ?? false)
                    {
                        yield return "transaction_id";
                    }
                }

                IEnumerable<string> EventList()
                {
                    yield return "rpc_completed";
                    yield return "sql_batch_completed";
                    yield return "sql_transaction";
                    if (_profile.IndividualStatements ?? false)
                    {
                        yield return "rpc_starting";
                        yield return "sql_batch_starting";
                        yield return "sp_statement_completed";
                    }
                }

                var actions = string.Join(",", ActionList().Select(s => "sqlserver." + s));
                var events = string.Join(",", EventList().Select(s => $"ADD EVENT sqlserver.{s} (ACTION({actions})) {query}"));

                Console.WriteLine($"Creating session {Output.Green(id)} {Output.Yellow(actions)}");
                await sql.ExecuteAsync($@"CREATE EVENT SESSION [{id}] ON SERVER {events}
                    WITH (MAX_MEMORY=1MB,EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS,MAX_DISPATCH_LATENCY=1 SECONDS)");
                Console.WriteLine($"Starting session {Output.Green(id)}");
                await sql.ExecuteAsync($"ALTER EVENT SESSION [{id}] ON SERVER STATE = START");
                try
                {
                    var streamer = new XELiveEventStreamer(_profile.ConnectionString, id);

                    var output = Console.Out;
                    var sb = new StringBuilder();

                    await streamer.ReadEventStream(DumpEvent, cancellationToken);

                    Task DumpEvent(IXEvent xe)
                    {
                        try
                        {
                            var log = FormatText(xe, out long txid);
                            if (log != null)
                            {
                                sb.Length = 0;
                                if ((_profile.ShowTransactionIds ?? false) && txid != 0)
                                {
                                    sb.Append(Output.Green($"({txid}) "));

                                    if (_transactions.TryGetValue(txid, out var txe) && txe != null)
                                    {
                                        DumpEvent(txe);
                                        _transactions[txid] = null;
                                    }
                                }

                                sb.Append('[');
                                sb.Append(xe.Name);

                                if (xe.Fields.TryGetValue("duration", out object oDur) && oDur is ulong dur && dur > 0)
                                {
                                    sb.Append(" in ");
                                    if (dur < 1000)
                                        sb.AppendFormat("{0,5}us", dur);
                                    else if (dur < 100000)
                                        sb.AppendFormat("{0,5:0.0}ms", dur / 1000.0);
                                    else
                                        sb.AppendFormat("{0,6:0.00}s", dur / 1000000.0);
                                }
                                sb.Append(']');

                                var evt = sb.ToString();

                                output.Write(Output.Yellow(xe.Timestamp.ToLocalTime().ToString("HH:mm:ss.ffff")));
                                output.Write(": ");
                                if (xe.Fields.TryGetValue("writes", out object oWr) && oWr is ulong wr && wr > 0)
                                    output.Write(Output.Bright.Magenta(evt));
                                else
                                    output.Write(Output.Dim(evt));
                                output.Write(' ');
                                output.WriteLine(log);
                            }
                        }
                        catch (Exception err)
                        {
                            Console.WriteLine($"{Output.Red("Error formatting output: ")} {err}");
                        }
                        return Task.CompletedTask;
                    }
                }
                catch (SqlException e) when (e.ErrorCode == -2146232060) // "A severe error occurred on the current command" - this is what happens when canceling
                {
                }
                finally
                {
                    Console.WriteLine($"Tearing down session {Output.Green(id)}");
                    await sql.ExecuteAsync($"ALTER EVENT SESSION [{id}] ON SERVER STATE = STOP");
                    await sql.ExecuteAsync($"DROP EVENT SESSION [{id}] ON SERVER");
                    Console.WriteLine($"Session teardown complete {Output.Green(id)}");
                }
            }
        }

        string FormatText(IXEvent e, out long txid)
        {
            string dbName = null, userName = null;

            if ((e.Fields.TryGetValue("transaction_id", out var otxid) ||
                e.Actions.TryGetValue("transaction_id", out otxid)) &&
                otxid is long id)
            {
                txid = id;
            }
            else
            {
                txid = 0;
            }

            if (e.Actions.TryGetValue("database_name", out var oDB) && oDB is string db)
            {
                if (_ignoredDatabases?.Contains(db) == true || _onlyDatabases?.Contains(db) == false)
                    return null;

                dbName = db;
            }

            if (e.Actions.TryGetValue("nt_username", out var oUser) && oUser is string user)
            {
                if (_ignoredUsers?.Contains(user) == true || _onlyUsers?.Contains(user) == false)
                    return null;

                userName = user;
            }

            if ((e.Fields.TryGetValue("statement", out var oStmt) || e.Fields.TryGetValue("batch_text", out oStmt))
                && oStmt is string stmt)
            {
                if (_ignoredStatements?.Contains(stmt) == true || _onlyStatements?.Contains(stmt) == false)
                    return null;

                if (IsPrefixInSet(_ignoredPrefixes, stmt) == true || IsPrefixInSet(_onlyPrefixes, stmt) == false)
                    return null;

                string formatted = null;

                if (stmt.StartsWith(s_paramStmtPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    formatted = FillParameters(stmt);
                    if (formatted != null)
                        formatted = CollapseSelects(formatted);
                }

                if (formatted == null)
                    formatted = Output.Yellow(CollapseSelects(stmt));


                return $"{Output.Cyan(dbName)}: {formatted}";
            }

            if (e.Fields.TryGetValue("transaction_state", out var txState) && txState is string txStateName)
            {
                if (txid != 0)
                {
                    if (txStateName == "Begin")
                    {
                        _transactions[txid] = e;
                        return null;
                    }
                    else if (_transactions.TryGetValue(txid, out var txe) && txe == null)
                    {
                        return $"{Output.Bright.Blue(txStateName)}";
                    }
                    else
                    {
                        return null;
                    }
                }
            }

            return e.ToString();
        }

        const string s_paramStmtPrefix = "exec sp_executesql N'";

        static bool SkipString(string sql, ref int i)
        {
            if (sql[i] != '\'')
                return false;

            i++;
            while (i < sql.Length)
            {
                if (sql[i++] == '\'')
                {
                    if (i < sql.Length && sql[i] == '\'')
                    {
                        // escaped, skip, continue
                        i++;
                        continue;
                    }
                    // correctly terminated string
                    return true;
                }
            }

            return false;
        }

        static readonly char[] s_skipChars = { '@', '\'' };

        string FillParameters(string sql)
        {
            // first run through the string and split it into segments
            int i = s_paramStmtPrefix.Length - 1;
            int stmtStart = i;
            if (!SkipString(sql, ref i)) return null;
            int stmtEnd = i;

            if (i + 2 > sql.Length || sql[i] != ',' || sql[i + 1] != 'N' || sql[i + 2] != '\'')
                return null;

            i += 2;
            int pdefStart = i + 1;
            if (!SkipString(sql, ref i)) return null;
            int pdefEnd = i - 1;
            var pdefs = sql[pdefStart..pdefEnd].Split(',');

            var paramVal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int paramIndex = 0;

            while (i < sql.Length)
            {
                if (sql[i] != ',')
                    return null;
                i++;
                int nameStart = i;
                int eq = sql.IndexOf('=', i + 1);
                string name;
                int valStart;
                if (eq == -1)
                {
                    name = pdefs[paramIndex].Split()[0];
                    valStart = i;
                }
                else
                {
                    name = sql.Substring(nameStart, eq - nameStart);
                    valStart = i = eq + 1;
                }
                if (i + 1 < sql.Length && (sql[i] == 'n' || sql[i] == 'N') && sql[i + 1] == '\'')
                {
                    // skip N, will be processed as string
                    i++;
                }
                if (i < sql.Length && sql[i] == '\'')
                {
                    if (!SkipString(sql, ref i)) return null;
                }
                else
                {
                    i = sql.IndexOf(',', i);
                    if (i == -1)
                        i = sql.Length;
                }
                paramVal[name] = sql.Substring(valStart, i - valStart);
                paramIndex++;
            }

            string stmt = sql.Substring(stmtStart + 1, stmtEnd - stmtStart - 2).Replace("''", "'");

            var sb = new StringBuilder();
            var rx = 0;

            while (rx < stmt.Length)
            {
                int s = stmt.IndexOfAny(s_skipChars, rx);
                if (s < 0)
                {
                    sb.Append(stmt, rx, stmt.Length - rx);
                    break;
                }

                sb.Append(stmt, rx, s - rx);

                rx = s;

                switch (stmt[s])
                {
                    case '\'':
                        // string literal, we must skip it to avoid expanding parameters inside
                        SkipString(stmt, ref rx);
                        string lit = stmt.Substring(s, rx - s);
                        sb.Append(Output.Bright.Magenta(lit));
                        break;

                    case '@':
                        // parameter reference
                        rx++;
                        while (rx < stmt.Length && (char.IsLetterOrDigit(stmt, rx) || stmt[rx] == '_'))
                            rx++;
                        string param = stmt.Substring(s, rx - s);
                        if (paramVal.TryGetValue(param, out var val))
                            sb.Append(Output.Bright.Green(val));
                        else
                            sb.Append(Output.Bright.Green(param));
                        break;

                    default:
                        // should not happen
                        sb.Append(Output.Red(stmt[rx++].ToString()));
                        break;
                }
            }

            return sb.ToString();
        }

        // regex fragment for a single fully qualified SELECT column with optional alias
        const string s_rexFragColumn = @"((?<table>[[\]\w_.]+)\.(?<column>[[\]\w_]+)(\s+AS\s+(?<alias>[[\]\w_+]+))?)";
        static readonly Regex s_rexSelect = new Regex(@$"(?<select>SELECT(\s+(TOP\s+\S+|DISTINCT))?)\s+
        ({s_rexFragColumn})(,\s+{s_rexFragColumn})*
        \s+(?<from>FROM)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline);

        string CollapseSelects(string stmt)
            => s_rexSelect.Replace(stmt, m =>
            {
                return m.Groups["select"] + " " + string.Join(", ",
                    m.Groups["table"].Captures
                        .GroupBy(c => c.Value)
                        .Select(g => $"{g.Key}.{Output.Dim("*")}")
                    ) + " " + m.Groups["from"];
            });
    }
}
