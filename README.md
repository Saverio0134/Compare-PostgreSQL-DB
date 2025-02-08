# Database Schema Comparison Tool

A C# application that compares two PostgreSQL database schemas and generates SQL update scripts to transform one schema into another. This tool is particularly useful for database version control and migration management.

## Features

- Compares two PostgreSQL database schemas
- Generates SQL scripts for:
  - Table creation and removal
  - Column addition, modification, and removal
  - Primary key modifications
  - Foreign key constraints management
- Automatically handles constraint dependencies by:
  - Removing foreign keys before structural changes
  - Adding foreign keys after structural changes
- Preserves data types and column properties
- Generates timestamped SQL scripts for tracking changes

## Prerequisites

- .NET Core / .NET Framework (version required for Microsoft.Extensions.Configuration)
- PostgreSQL database
- Npgsql NuGet package
- Microsoft.Extensions.Configuration NuGet package

## Configuration

Create an `appsettings.json` file in the project directory with the following structure:

```json
{
  "ConnectionStrings": {
    "OldDb": "Your_Old_Database_Connection_String",
    "NewDb": "Your_New_Database_Connection_String"
  }
}
```

Replace the connection strings with your PostgreSQL database connections.

## Usage

1. Configure your database connections in `appsettings.json`
2. Run the application
3. The tool will:
   - Compare the schemas of both databases
   - Generate SQL update scripts if differences are found
   - Save the scripts in a `SqlScripts` folder with a timestamp
   - Display the path to the generated script file

## Output

The generated SQL scripts are organized in the following order:
1. Foreign key constraint removal
2. Table structure modifications
3. Foreign key constraint creation

Scripts are saved in the `SqlScripts` directory with the naming format: `UpdateScript_YYYYMMDD_HHMMSS.sql`

## Technical Details

### Schema Comparison Includes:
- Table structure
- Column properties:
  - Data types
  - Maximum lengths
  - Nullable status
  - Default values
  - Numeric precision and scale
- Primary keys
- Foreign key constraints

### Case Sensitivity Handling
- The tool automatically handles case-sensitive object names by adding proper quotation marks
- Names starting with uppercase letters are quoted in the generated SQL

## Error Handling

- The application includes comprehensive error handling
- Connection issues and schema comparison errors are caught and displayed
- The program will not terminate abruptly in case of errors

## Notes

- All schema comparisons are done against the 'public' schema
- The tool preserves data while modifying schema structures
- Generated scripts include comments for better readability and tracking

## Limitations

- Only supports PostgreSQL databases
- Works with tables in the 'public' schema
- Does not handle data migration
- Does not manage indexes or views

## Contributing

Feel free to submit issues and enhancement requests.
