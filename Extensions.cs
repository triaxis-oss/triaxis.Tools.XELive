using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace XELive
{
    static class Extensions
    {
        public static async Task<int> ExecuteAsync(this SqlConnection sql, string command, object parameters = null)
        {
            using (var cmd = sql.CreateCommand())
            {
                cmd.Initialize(command, parameters);
                return await cmd.ExecuteNonQueryAsync();
            }
        }

        public static Task ExecuteReaderAsync(this SqlConnection sql, string command, Action<SqlDataReader> rowHandler)
            => sql.ExecuteReaderAsync(command, null, rowHandler);
        public static async Task ExecuteReaderAsync(this SqlConnection sql, string command, object parameters, Action<SqlDataReader> rowHandler)
        {
            using (var cmd = sql.CreateCommand())
            {
                cmd.Initialize(command, parameters);
                using (var rdr = await cmd.ExecuteReaderAsync())
                {
                    while (await rdr.ReadAsync())
                    {
                        rowHandler(rdr);
                    }
                }
            }
        }

        public static void Initialize(this SqlCommand command, string text, object parameters)
        {
            command.CommandText = text;
            if (parameters != null)
            {
                foreach (var prop in parameters.GetType().GetProperties())
                    command.Parameters.Add(new SqlParameter(prop.Name, prop.GetValue(parameters)));
            }
        }
    }
}
