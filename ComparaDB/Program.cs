using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;

class Program
{
    static void Main()
    {
        try
        {
            var config = LoadConfiguration();

            string connStrOld = config.GetConnectionString("OldDb");
            string connStrNew = config.GetConnectionString("NewDb");

            var oldSchema = GetDatabaseSchema(connStrOld);
            var newSchema = GetDatabaseSchema(connStrNew);

            GenerateUpdateScripts(oldSchema, newSchema);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("Premi un tasto per chiudere.");
            Console.ReadKey();
        }
    }
    
    static IConfiguration LoadConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory()) // Imposta il percorso
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true); // Carica il file JSON

        return builder.Build();
    }

    static Dictionary<string, TableInfo> GetDatabaseSchema(string connectionString)
    {
        var schema = new Dictionary<string, TableInfo>();

        using (var conn = new NpgsqlConnection(connectionString))
        {
            conn.Open();

            // Get columns info
            using (var cmd = new NpgsqlCommand(@"
                SELECT 
                    c.table_name, 
                    c.column_name,
                    c.data_type,
                    c.character_maximum_length,
                    c.numeric_precision,
                    c.numeric_scale,
                    c.is_nullable,
                    c.column_default
                FROM information_schema.columns c
                WHERE c.table_schema = 'public'
                ORDER BY c.table_name, c.ordinal_position;", conn))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    string table = reader.GetString(0);
                    var column = new ColumnInfo
                    {
                        Name = reader.GetString(1),
                        DataType = reader.GetString(2),
                        MaxLength = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        NumericPrecision = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        NumericScale = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        IsNullable = reader.GetString(6) == "YES",
                        DefaultValue = reader.IsDBNull(7) ? null : reader.GetString(7)
                    };

                    if (!schema.ContainsKey(table))
                        schema[table] = new TableInfo
                        {
                            Name = table,
                            Columns = new List<ColumnInfo>(),
                            PrimaryKey = new List<string>(),
                            ForeignKeys = new List<ForeignKeyInfo>()
                        };

                    schema[table].Columns.Add(column);
                }
            }

            // Get primary keys
            using (var cmd = new NpgsqlCommand(@"
                SELECT 
                    tc.table_name, 
                    kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu 
                    ON tc.constraint_name = kcu.constraint_name
                WHERE tc.constraint_type = 'PRIMARY KEY'
                    AND tc.table_schema = 'public'
                ORDER BY tc.table_name, kcu.ordinal_position;", conn))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    string table = reader.GetString(0);
                    string column = reader.GetString(1);
                    if (schema.ContainsKey(table))
                    {
                        schema[table].PrimaryKey.Add(column);
                    }
                }
            }

            // Get foreign keys
            using (var cmd = new NpgsqlCommand(@"
                SELECT 
                    tc.table_name,
                    kcu.column_name,
                    ccu.table_name AS referenced_table,
                    ccu.column_name AS referenced_column,
                    tc.constraint_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                    ON tc.constraint_name = kcu.constraint_name
                JOIN information_schema.constraint_column_usage ccu
                    ON tc.constraint_name = ccu.constraint_name
                WHERE tc.constraint_type = 'FOREIGN KEY'
                    AND tc.table_schema = 'public';", conn))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    string table = reader.GetString(0);
                    if (schema.ContainsKey(table))
                    {
                        schema[table].ForeignKeys.Add(new ForeignKeyInfo
                        {
                            ConstraintName = reader.GetString(4),
                            ColumnName = reader.GetString(1),
                            ReferencedTable = reader.GetString(2),
                            ReferencedColumn = reader.GetString(3)
                        });
                    }
                }
            }
        }

        return schema;
    }


    static void GenerateUpdateScripts(Dictionary<string, TableInfo> oldSchema, Dictionary<string, TableInfo> newSchema)
    {
        var oldTables = oldSchema.Keys.ToHashSet();
        var newTables = newSchema.Keys.ToHashSet();
        var scripts = new StringBuilder();
        var fkScriptsToRemove = new StringBuilder();
        var fkScriptsToAdd = new StringBuilder();

        // Rimuovi Foreign Keys PRIMA di modificare le tabelle
        foreach (var table in oldTables)
        {
            if (!newSchema.ContainsKey(table)) continue;

            foreach (var fk in oldSchema[table].ForeignKeys)
            {
                if (!newSchema[table].ForeignKeys.Any(newFk => newFk.ConstraintName == fk.ConstraintName))
                {
                    fkScriptsToRemove.AppendLine($"-- Rimozione foreign key da {FormatName(table)}");
                    fkScriptsToRemove.AppendLine(
                        $"ALTER TABLE {FormatName(table)} DROP CONSTRAINT IF EXISTS {FormatName(fk.ConstraintName)};");
                }
            }
        }

        // Rimuovi tabelle non più presenti
        var tablesToRemove = oldTables.Except(newTables).ToList();
        foreach (var table in tablesToRemove)
        {
            scripts.AppendLine($"-- Rimozione tabella {FormatName(table)}");
            scripts.AppendLine($"DROP TABLE IF EXISTS {FormatName(table)} CASCADE;");
        }

        // Creazione nuove tabelle
        foreach (var table in newTables.Except(oldTables))
        {
            scripts.AppendLine($"-- Creazione tabella {FormatName(table)}");
            scripts.AppendLine($"CREATE TABLE {FormatName(table)} (");

            var columns = newSchema[table].Columns;
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                scripts.Append($"    {FormatName(col.Name)} {GetColumnDefinition(col)}");
                scripts.AppendLine(i < columns.Count - 1 ? "," : "");
            }

            scripts.AppendLine(");");

            // Aggiunta Primary Key
            if (newSchema[table].PrimaryKey.Any())
            {
                scripts.AppendLine(
                    $"ALTER TABLE {FormatName(table)} ADD PRIMARY KEY ({string.Join(", ", newSchema[table].PrimaryKey.Select(FormatName))});");
            }
        }

        // Modifiche tabelle esistenti
        foreach (var table in oldTables.Intersect(newTables))
        {
            var oldColumns = oldSchema[table].Columns.ToDictionary(c => c.Name);
            var newColumns = newSchema[table].Columns.ToDictionary(c => c.Name);

            // Rimuovi colonne non più presenti
            foreach (var colName in oldColumns.Keys.Except(newColumns.Keys))
            {
                scripts.AppendLine($"-- Rimozione colonna {FormatName(colName)} da {FormatName(table)}");
                scripts.AppendLine($"ALTER TABLE {FormatName(table)} DROP COLUMN IF EXISTS {FormatName(colName)};");
            }

            // Aggiungi nuove colonne
            foreach (var col in newColumns.Values.Where(c => !oldColumns.ContainsKey(c.Name)))
            {
                scripts.AppendLine($"-- Aggiunta colonna {FormatName(col.Name)} a {FormatName(table)}");
                scripts.AppendLine(
                    $"ALTER TABLE {FormatName(table)} ADD COLUMN {FormatName(col.Name)} {GetColumnDefinition(col)};");
            }

            // Modifica colonne esistenti
            foreach (var newCol in newColumns.Values.Where(c => oldColumns.ContainsKey(c.Name)))
            {
                var oldCol = oldColumns[newCol.Name];
                if (!AreColumnsEqual(oldCol, newCol))
                {
                    scripts.AppendLine($"-- Modifica colonna {FormatName(newCol.Name)} in {FormatName(table)}");
                    scripts.AppendLine(
                        $"ALTER TABLE {FormatName(table)} ALTER COLUMN {FormatName(newCol.Name)} TYPE {GetColumnDefinition(newCol)};");

                    if (oldCol.IsNullable != newCol.IsNullable)
                    {
                        scripts.AppendLine(
                            $"ALTER TABLE {FormatName(table)} ALTER COLUMN {FormatName(newCol.Name)} {(newCol.IsNullable ? "DROP NOT NULL" : "SET NOT NULL")};");
                    }

                    if (oldCol.DefaultValue != newCol.DefaultValue)
                    {
                        if (newCol.DefaultValue != null)
                            scripts.AppendLine(
                                $"ALTER TABLE {FormatName(table)} ALTER COLUMN {FormatName(newCol.Name)} SET DEFAULT {newCol.DefaultValue};");
                        else
                            scripts.AppendLine(
                                $"ALTER TABLE {FormatName(table)} ALTER COLUMN {FormatName(newCol.Name)} DROP DEFAULT;");
                    }
                }
            }

            // Aggiorna Primary Key se necessario
            if (!oldSchema[table].PrimaryKey.SequenceEqual(newSchema[table].PrimaryKey))
            {
                scripts.AppendLine($"-- Aggiornamento Primary Key per {FormatName(table)}");
                scripts.AppendLine(
                    $"ALTER TABLE {FormatName(table)} DROP CONSTRAINT IF EXISTS {FormatName(table)}_pkey;");
                if (newSchema[table].PrimaryKey.Any())
                {
                    scripts.AppendLine(
                        $"ALTER TABLE {FormatName(table)} ADD PRIMARY KEY ({string.Join(", ", newSchema[table].PrimaryKey.Select(FormatName))});");
                }
            }
        }
        

        // Aggiunta Foreign Keys DOPO tutte le modifiche
        foreach (var table in newTables)
        {

            foreach (var fk in newSchema[table].ForeignKeys)
            {
                if (!oldSchema.ContainsKey(table) || !oldSchema[table].ForeignKeys.Any(oldFk => oldFk.ConstraintName == fk.ConstraintName))
                {
                    fkScriptsToAdd.AppendLine($"-- Aggiunta foreign key a {FormatName(table)}");
                    fkScriptsToAdd.AppendLine(
                        $"ALTER TABLE {FormatName(table)} ADD CONSTRAINT {FormatName(fk.ConstraintName)} " +
                        $"FOREIGN KEY ({FormatName(fk.ColumnName)}) REFERENCES {FormatName(fk.ReferencedTable)}({FormatName(fk.ReferencedColumn)});");
                }
            }
        }

        //// Stampa finale degli script
        //if(fkScriptsToRemove.ToString().Length > 0) Console.WriteLine(fkScriptsToRemove.ToString());
        //if (scripts.ToString().Length > 0) Console.WriteLine(scripts.ToString());
        //if (fkScriptsToAdd.ToString().Length > 0) Console.WriteLine(fkScriptsToAdd.ToString());



        

        if (scripts.Length == 0 && fkScriptsToAdd.Length == 0 && fkScriptsToRemove.Length == 0)
        {
            Console.WriteLine("Nessuna differenza trovata tra i due Database.");
        }
        else
        {
            // Creazione della cartella per salvare gli script
            string folderPath = "SqlScripts";
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            // Genera un timestamp per rendere il file univoco
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string filePath = Path.Combine(folderPath, $"UpdateScript_{timestamp}.sql");

            // Scrittura di tutti gli script in un unico file
            using (StreamWriter writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                if (fkScriptsToRemove.Length > 0)
                    writer.WriteLine(fkScriptsToRemove.ToString());

                if (scripts.Length > 0)
                    writer.WriteLine(scripts.ToString());

                if (fkScriptsToAdd.Length > 0)
                    writer.WriteLine(fkScriptsToAdd.ToString());
            }
            Console.WriteLine($"Script di aggiornamento generato nella cartella SqlScripts: {filePath}");
        }

    }

    static string FormatName(string name)
    {
        return Char.IsUpper(name[0]) ? $"\"{name}\"" : name;
    }

    static string GetColumnDefinition(ColumnInfo column)
    {
        var def = new StringBuilder(column.DataType);

        if (column.MaxLength.HasValue)
            def.Append($"({column.MaxLength})");

        if (!column.IsNullable)
            def.Append(" NOT NULL");

        if (column.DefaultValue != null)
            def.Append($" DEFAULT {column.DefaultValue}");

        return def.ToString();
    }

    static bool AreColumnsEqual(ColumnInfo col1, ColumnInfo col2)
    {
        return col1.DataType == col2.DataType &&
               col1.MaxLength == col2.MaxLength &&
               col1.NumericPrecision == col2.NumericPrecision &&
               col1.NumericScale == col2.NumericScale &&
               col1.IsNullable == col2.IsNullable &&
               col1.DefaultValue == col2.DefaultValue;
    }
}

class TableInfo
{
    public string Name { get; set; }
    public List<ColumnInfo> Columns { get; set; }
    public List<string> PrimaryKey { get; set; }
    public List<ForeignKeyInfo> ForeignKeys { get; set; }
}

class ColumnInfo
{
    public string Name { get; set; }
    public string DataType { get; set; }
    public int? MaxLength { get; set; }
    public int? NumericPrecision { get; set; }
    public int? NumericScale { get; set; }
    public bool IsNullable { get; set; }
    public string DefaultValue { get; set; }
}

class ForeignKeyInfo
{
    public string ConstraintName { get; set; }
    public string ColumnName { get; set; }
    public string ReferencedTable { get; set; }
    public string ReferencedColumn { get; set; }
}