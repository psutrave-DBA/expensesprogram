using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Host.UseWindowsService();

var app = builder.Build();

// ============================================================
// DATABASE CONFIGURATION
// ============================================================

const string serverName = "localhost";
const string databaseName = "Personal_DB";
const string tableName = "MonthlyExpenses_KSA";

string connectionString =
    $"Server={serverName};" +
    $"Database={databaseName};" +
    $"Trusted_Connection=True;" +
    $"TrustServerCertificate=True;" +
    $"Connect Timeout=10;";

app.Urls.Add("http://localhost:5000");


// ============================================================
// HELPER
// ============================================================

SqlConnection CreateConnection()
{
    return new SqlConnection(connectionString);
}


// ============================================================
// HTML FILES
// ============================================================

app.MapGet("/", () =>
{
    string file = Path.Combine(
        app.Environment.ContentRootPath,
        "wwwroot",
        "form.html");

    return Results.File(file, "text/html");
});


app.MapGet("/report", () =>
{
    string file = Path.Combine(
        app.Environment.ContentRootPath,
        "wwwroot",
        "report.html");

    return Results.File(file, "text/html");
});


app.MapGet("/financial", () =>
{
    string file = Path.Combine(
        app.Environment.ContentRootPath,
        "wwwroot",
        "financial.html");

    return Results.File(file, "text/html");
});


// ============================================================
// API: AVAILABLE REPORT MONTHS
//
// Used by report.html
// ============================================================

app.MapGet("/api/report-months", async () =>
{
    try
    {
        var months = new List<object>();

        await using var conn = CreateConnection();
        await conn.OpenAsync();

        string query = $@"
            SELECT DISTINCT
                DATEFROMPARTS(
                    YEAR([ExpenseDate]),
                    MONTH([ExpenseDate]),
                    1
                ) AS [MonthStart]

            FROM [dbo].[{tableName}]

            WHERE [ExpenseDate] IS NOT NULL

            UNION

            SELECT
                DATEFROMPARTS(
                    YEAR(GETDATE()),
                    MONTH(GETDATE()),
                    1
                )

            ORDER BY [MonthStart] DESC;";

        await using var cmd = new SqlCommand(query, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            DateTime monthStart =
                Convert.ToDateTime(reader["MonthStart"]);

            months.Add(new
            {
                value = monthStart.ToString("yyyy-MM"),
                label = monthStart.ToString("MMMM yyyy")
            });
        }

        return Results.Json(new
        {
            success = true,
            months
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Error loading report months.");

        return Results.Json(
            new
            {
                success = false,
                message = "Unable to load report months.",
                error = ex.Message
            },
            statusCode: 500);
    }
});


// ============================================================
// API: ANALYTICS REPORT
//
// Example:
// /api/analytics?month=2026-10
//
// Selected month:
// - Total
// - Category
// - Payment method
//
// Trend:
// Selected month + previous 5 months
// ============================================================

app.MapGet("/api/analytics", async (HttpRequest request) =>
{
    try
    {
        string monthParameter =
            request.Query["month"].ToString().Trim();

        DateTime selectedMonth;

        if (!string.IsNullOrWhiteSpace(monthParameter))
        {
            if (!DateTime.TryParseExact(
                    monthParameter,
                    "yyyy-MM",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out selectedMonth))
            {
                return Results.BadRequest(
                    new
                    {
                        success = false,
                        message = "Invalid month. Use YYYY-MM."
                    });
            }

            selectedMonth = new DateTime(
                selectedMonth.Year,
                selectedMonth.Month,
                1);
        }
        else
        {
            selectedMonth = new DateTime(
                DateTime.Now.Year,
                DateTime.Now.Month,
                1);
        }


        decimal selectedMonthTotalSar = 0;
        decimal selectedMonthTotalUsd = 0;

        var monthlyLabels = new List<string>();
        var monthlyDataSar = new List<decimal>();
        var monthlyDataUsd = new List<decimal>();

        var categoryLabels = new List<string>();
        var categoryDataSar = new List<decimal>();
        var categoryDataUsd = new List<decimal>();

        var paymentLabels = new List<string>();
        var paymentDataSar = new List<decimal>();
        var paymentDataUsd = new List<decimal>();


        await using var conn = CreateConnection();
        await conn.OpenAsync();


        // ========================================================
        // SELECTED MONTH TOTAL
        // ========================================================

        string qSelectedMonth = $@"
            SELECT
                ISNULL([Currency], 'SAR') AS [Currency],
                ISNULL(SUM([Amount]), 0) AS [TotalAmount]

            FROM [dbo].[{tableName}]

            WHERE [ExpenseDate] >= @SelectedMonth
              AND [ExpenseDate] <
                    DATEADD(month, 1, @SelectedMonth)

            GROUP BY [Currency];";

        await using (var cmd = new SqlCommand(
            qSelectedMonth,
            conn))
        {
            cmd.Parameters.Add(
                "@SelectedMonth",
                System.Data.SqlDbType.DateTime).Value =
                selectedMonth;

            await using var reader =
                await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                string currency =
                    reader["Currency"]?.ToString() ?? "SAR";

                decimal amount =
                    Convert.ToDecimal(
                        reader["TotalAmount"]);

                if (currency.Equals(
                        "USD",
                        StringComparison.OrdinalIgnoreCase))
                {
                    selectedMonthTotalUsd += amount;
                }
                else
                {
                    selectedMonthTotalSar += amount;
                }
            }
        }


        // ========================================================
        // SIX MONTH TREND
        // ========================================================

        string q6Months = $@"
            WITH Months AS
            (
                SELECT
                    DATEADD(
                        month,
                        -5,
                        @SelectedMonth
                    ) AS MonthStart

                UNION ALL

                SELECT
                    DATEADD(
                        month,
                        1,
                        MonthStart
                    )

                FROM Months

                WHERE MonthStart < @SelectedMonth
            ),

            ExpenseTotals AS
            (
                SELECT

                    DATEFROMPARTS(
                        YEAR([ExpenseDate]),
                        MONTH([ExpenseDate]),
                        1
                    ) AS MonthStart,

                    ISNULL([Currency], 'SAR') AS Currency,

                    SUM([Amount]) AS TotalAmount

                FROM [dbo].[{tableName}]

                WHERE [ExpenseDate] >=
                    DATEADD(
                        month,
                        -5,
                        @SelectedMonth)

                  AND [ExpenseDate] <
                    DATEADD(
                        month,
                        1,
                        @SelectedMonth)

                GROUP BY

                    DATEFROMPARTS(
                        YEAR([ExpenseDate]),
                        MONTH([ExpenseDate]),
                        1
                    ),

                    ISNULL([Currency], 'SAR')
            )

            SELECT

                m.MonthStart,

                ISNULL(
                    s.TotalAmount,
                    0
                ) AS SarAmount,

                ISNULL(
                    u.TotalAmount,
                    0
                ) AS UsdAmount

            FROM Months m

            LEFT JOIN ExpenseTotals s
                ON s.MonthStart = m.MonthStart
               AND s.Currency = 'SAR'

            LEFT JOIN ExpenseTotals u
                ON u.MonthStart = m.MonthStart
               AND u.Currency = 'USD'

            ORDER BY m.MonthStart

            OPTION (MAXRECURSION 10);";

        await using (var cmd = new SqlCommand(
            q6Months,
            conn))
        {
            cmd.Parameters.Add(
                "@SelectedMonth",
                System.Data.SqlDbType.DateTime).Value =
                selectedMonth;

            await using var reader =
                await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                DateTime monthStart =
                    Convert.ToDateTime(
                        reader["MonthStart"]);

                decimal sar =
                    Convert.ToDecimal(
                        reader["SarAmount"]);

                decimal usd =
                    Convert.ToDecimal(
                        reader["UsdAmount"]);

                monthlyLabels.Add(
                    monthStart.ToString("MMM yyyy"));

                monthlyDataSar.Add(sar);
                monthlyDataUsd.Add(usd);
            }
        }


        // ========================================================
        // CATEGORY
        // ========================================================

        string qCategory = $@"
            SELECT

                ISNULL(
                    NULLIF(
                        LTRIM(RTRIM([Category])),
                        ''
                    ),
                    'Other'
                ) AS [Category],

                ISNULL(
                    [Currency],
                    'SAR'
                ) AS [Currency],

                SUM([Amount]) AS [TotalAmount]

            FROM [dbo].[{tableName}]

            WHERE [ExpenseDate] >= @SelectedMonth
              AND [ExpenseDate] <
                    DATEADD(
                        month,
                        1,
                        @SelectedMonth)

            GROUP BY

                ISNULL(
                    NULLIF(
                        LTRIM(RTRIM([Category])),
                        ''
                    ),
                    'Other'
                ),

                ISNULL(
                    [Currency],
                    'SAR'
                );";


        var categorySar =
            new Dictionary<string, decimal>(
                StringComparer.OrdinalIgnoreCase);

        var categoryUsd =
            new Dictionary<string, decimal>(
                StringComparer.OrdinalIgnoreCase);


        await using (var cmd = new SqlCommand(
            qCategory,
            conn))
        {
            cmd.Parameters.Add(
                "@SelectedMonth",
                System.Data.SqlDbType.DateTime).Value =
                selectedMonth;

            await using var reader =
                await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                string category =
                    reader["Category"]?.ToString()
                    ?? "Other";

                string currency =
                    reader["Currency"]?.ToString()
                    ?? "SAR";

                decimal amount =
                    Convert.ToDecimal(
                        reader["TotalAmount"]);

                if (currency.Equals(
                        "USD",
                        StringComparison.OrdinalIgnoreCase))
                {
                    categoryUsd[category] =
                        categoryUsd.GetValueOrDefault(
                            category) + amount;
                }
                else
                {
                    categorySar[category] =
                        categorySar.GetValueOrDefault(
                            category) + amount;
                }
            }
        }


        var allCategories =
            categorySar.Keys
                .Union(
                    categoryUsd.Keys,
                    StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(
                    category =>
                        categorySar.GetValueOrDefault(
                            category)
                        +
                        categoryUsd.GetValueOrDefault(
                            category))
                .ToList();


        foreach (string category in allCategories)
        {
            categoryLabels.Add(category);

            categoryDataSar.Add(
                categorySar.GetValueOrDefault(
                    category));

            categoryDataUsd.Add(
                categoryUsd.GetValueOrDefault(
                    category));
        }


        // ========================================================
        // PAYMENT METHOD
        // ========================================================

        string qPayment = $@"
            SELECT

                ISNULL(
                    NULLIF(
                        LTRIM(RTRIM([PaymentMethod])),
                        ''
                    ),
                    'Cash'
                ) AS [PaymentMethod],

                ISNULL(
                    [Currency],
                    'SAR'
                ) AS [Currency],

                SUM([Amount]) AS [TotalAmount]

            FROM [dbo].[{tableName}]

            WHERE [ExpenseDate] >= @SelectedMonth
              AND [ExpenseDate] <
                    DATEADD(
                        month,
                        1,
                        @SelectedMonth)

            GROUP BY

                ISNULL(
                    NULLIF(
                        LTRIM(RTRIM([PaymentMethod])),
                        ''
                    ),
                    'Cash'
                ),

                ISNULL(
                    [Currency],
                    'SAR'
                );";


        var paymentSar =
            new Dictionary<string, decimal>(
                StringComparer.OrdinalIgnoreCase);

        var paymentUsd =
            new Dictionary<string, decimal>(
                StringComparer.OrdinalIgnoreCase);


        await using (var cmd = new SqlCommand(
            qPayment,
            conn))
        {
            cmd.Parameters.Add(
                "@SelectedMonth",
                System.Data.SqlDbType.DateTime).Value =
                selectedMonth;

            await using var reader =
                await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                string paymentMethod =
                    reader["PaymentMethod"]?.ToString()
                    ?? "Cash";

                string currency =
                    reader["Currency"]?.ToString()
                    ?? "SAR";

                decimal amount =
                    Convert.ToDecimal(
                        reader["TotalAmount"]);

                if (currency.Equals(
                        "USD",
                        StringComparison.OrdinalIgnoreCase))
                {
                    paymentUsd[paymentMethod] =
                        paymentUsd.GetValueOrDefault(
                            paymentMethod) + amount;
                }
                else
                {
                    paymentSar[paymentMethod] =
                        paymentSar.GetValueOrDefault(
                            paymentMethod) + amount;
                }
            }
        }


        var allPaymentMethods =
            paymentSar.Keys
                .Union(
                    paymentUsd.Keys,
                    StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(
                    method =>
                        paymentSar.GetValueOrDefault(
                            method)
                        +
                        paymentUsd.GetValueOrDefault(
                            method))
                .ToList();


        foreach (string method in allPaymentMethods)
        {
            paymentLabels.Add(method);

            paymentDataSar.Add(
                paymentSar.GetValueOrDefault(
                    method));

            paymentDataUsd.Add(
                paymentUsd.GetValueOrDefault(
                    method));
        }


        return Results.Json(new
        {
            success = true,

            selectedMonth =
                selectedMonth.ToString("yyyy-MM"),

            selectedMonthLabel =
                selectedMonth.ToString("MMMM yyyy"),

            selectedMonthTotalSar,
            selectedMonthTotalUsd,

            monthlyLabels,
            monthlyDataSar,
            monthlyDataUsd,

            categoryLabels,
            categoryDataSar,
            categoryDataUsd,

            paymentLabels,
            paymentDataSar,
            paymentDataUsd,

            generatedAt =
                DateTime.Now
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Error loading expense analytics.");

        return Results.Json(
            new
            {
                success = false,
                message =
                    "Unable to load expense analytics.",
                error = ex.Message
            },
            statusCode: 500);
    }
});


// ============================================================
// API: FINANCIAL YEARS
//
// Returns all years available in the database.
// ============================================================

app.MapGet("/api/financial-years", async () =>
{
    try
    {
        var years = new List<int>();

        await using var conn = CreateConnection();
        await conn.OpenAsync();

        string query = $@"
            SELECT DISTINCT
                YEAR([ExpenseDate]) AS [ExpenseYear]

            FROM [dbo].[{tableName}]

            WHERE [ExpenseDate] IS NOT NULL

            ORDER BY [ExpenseYear] DESC;";

        await using var cmd = new SqlCommand(query, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            years.Add(
                Convert.ToInt32(
                    reader["ExpenseYear"]));
        }

        // Always include current year.
        int currentYear = DateTime.Now.Year;

        if (!years.Contains(currentYear))
        {
            years.Add(currentYear);
            years.Sort((a, b) => b.CompareTo(a));
        }

        return Results.Json(new
        {
            success = true,
            years
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Error loading financial years.");

        return Results.Json(
            new
            {
                success = false,
                message =
                    "Unable to load financial years.",
                error = ex.Message
            },
            statusCode: 500);
    }
});


// ============================================================
// API: FINANCIAL ANALYSIS
//
// Example:
// /api/financial?year=2026&month=10
//
// Returns detailed records for selected month/year.
// ============================================================

app.MapGet("/api/financial", async (HttpRequest request) =>
{
    try
    {
        string yearText =
            request.Query["year"].ToString().Trim();

        string monthText =
            request.Query["month"].ToString().Trim();


        if (!int.TryParse(
                yearText,
                out int year) ||
            year < 2000 ||
            year > 2100)
        {
            year = DateTime.Now.Year;
        }


        if (!int.TryParse(
                monthText,
                out int month) ||
            month < 1 ||
            month > 12)
        {
            month = DateTime.Now.Month;
        }


        DateTime startDate =
            new DateTime(
                year,
                month,
                1);

        DateTime endDate =
            startDate.AddMonths(1);


        var records = new List<object>();

        decimal totalSar = 0;
        decimal totalUsd = 0;

        int recordCount = 0;


        await using var conn = CreateConnection();
        await conn.OpenAsync();


        // ========================================================
        // Detailed financial data
        //
        // Ordered by:
        // Expense Date
        // Category
        // Description
        // ========================================================

        string query = $@"
            SELECT

                [ExpenseDate],

                ISNULL(
                    NULLIF(
                        LTRIM(RTRIM([Category])),
                        ''
                    ),
                    'Other'
                ) AS [Category],

                ISNULL(
                    NULLIF(
                        LTRIM(RTRIM([Description])),
                        ''
                    ),
                    ''
                ) AS [Description],

                [Amount],

                ISNULL(
                    NULLIF(
                        LTRIM(RTRIM([Currency])),
                        ''
                    ),
                    'SAR'
                ) AS [Currency],

                ISNULL(
                    NULLIF(
                        LTRIM(RTRIM([PaymentMethod])),
                        ''
                    ),
                    'Cash'
                ) AS [PaymentMethod],

                [CreatedAt]

            FROM [dbo].[{tableName}]

            WHERE [ExpenseDate] >= @StartDate
              AND [ExpenseDate] < @EndDate

            ORDER BY

                [ExpenseDate] ASC,
                [Category] ASC,
                [Description] ASC,
                [CreatedAt] ASC;";


        await using var cmd =
            new SqlCommand(query, conn);

        cmd.Parameters.Add(
            "@StartDate",
            System.Data.SqlDbType.DateTime).Value =
            startDate;

        cmd.Parameters.Add(
            "@EndDate",
            System.Data.SqlDbType.DateTime).Value =
            endDate;


        await using var reader =
            await cmd.ExecuteReaderAsync();


        while (await reader.ReadAsync())
        {
            DateTime expenseDate =
                Convert.ToDateTime(
                    reader["ExpenseDate"]);

            string category =
                reader["Category"]?.ToString()
                ?? "Other";

            string description =
                reader["Description"]?.ToString()
                ?? "";

            decimal amount =
                Convert.ToDecimal(
                    reader["Amount"]);

            string currency =
                reader["Currency"]?.ToString()
                ?? "SAR";

            string paymentMethod =
                reader["PaymentMethod"]?.ToString()
                ?? "Cash";


            if (currency.Equals(
                    "USD",
                    StringComparison.OrdinalIgnoreCase))
            {
                totalUsd += amount;
            }
            else
            {
                totalSar += amount;
            }


            recordCount++;


            records.Add(new
            {
                expenseDate =
                    expenseDate.ToString(
                        "yyyy-MM-dd"),

                category,

                description,

                amount,

                currency,

                paymentMethod
            });
        }


        return Results.Json(new
        {
            success = true,

            year,
            month,

            monthLabel =
                startDate.ToString(
                    "MMMM yyyy"),

            startDate =
                startDate.ToString(
                    "yyyy-MM-dd"),

            endDate =
                endDate.AddDays(-1).ToString(
                    "yyyy-MM-dd"),

            recordCount,

            totalSar,
            totalUsd,

            records
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Error loading financial analysis.");

        return Results.Json(
            new
            {
                success = false,
                message =
                    "Unable to load financial data.",
                error = ex.Message
            },
            statusCode: 500);
    }
});


// ============================================================
// DOWNLOAD CSV
//
// Example:
// /api/financial/download?year=2026&month=10
//
// Generates a CSV directly from SQL Server data.
// ============================================================

app.MapGet(
    "/api/financial/download",
    async (HttpRequest request) =>
{
    try
    {
        string yearText =
            request.Query["year"].ToString().Trim();

        string monthText =
            request.Query["month"].ToString().Trim();


        if (!int.TryParse(
                yearText,
                out int year) ||
            year < 2000 ||
            year > 2100)
        {
            return Results.BadRequest(
                "Invalid year.");
        }


        if (!int.TryParse(
                monthText,
                out int month) ||
            month < 1 ||
            month > 12)
        {
            return Results.BadRequest(
                "Invalid month.");
        }


        DateTime startDate =
            new DateTime(
                year,
                month,
                1);

        DateTime endDate =
            startDate.AddMonths(1);


        var csv = new StringBuilder();


        // --------------------------------------------------------
        // UTF-8 BOM
        //
        // Helps Excel recognize UTF-8 correctly.
        // --------------------------------------------------------

        csv.Append('\uFEFF');


        // --------------------------------------------------------
        // CSV Header
        // --------------------------------------------------------

        csv.AppendLine(
            "Expense Date,Category,Description,Amount,Currency,Payment Method");


        await using var conn = CreateConnection();
        await conn.OpenAsync();


        string query = $@"
            SELECT

                [ExpenseDate],

                ISNULL(
                    NULLIF(
                        LTRIM(RTRIM([Category])),
                        ''
                    ),
                    'Other'
                ) AS [Category],

                ISNULL(
                    NULLIF(
                        LTRIM(RTRIM([Description])),
                        ''
                    ),
                    ''
                ) AS [Description],

                [Amount],

                ISNULL(
                    NULLIF(
                        LTRIM(RTRIM([Currency])),
                        ''
                    ),
                    'SAR'
                ) AS [Currency],

                ISNULL(
                    NULLIF(
                        LTRIM(RTRIM([PaymentMethod])),
                        ''
                    ),
                    'Cash'
                ) AS [PaymentMethod]

            FROM [dbo].[{tableName}]

            WHERE [ExpenseDate] >= @StartDate
              AND [ExpenseDate] < @EndDate

            ORDER BY

                [ExpenseDate] ASC,
                [Category] ASC,
                [Description] ASC;";


        await using var cmd =
            new SqlCommand(query, conn);


        cmd.Parameters.Add(
            "@StartDate",
            System.Data.SqlDbType.DateTime).Value =
            startDate;

        cmd.Parameters.Add(
            "@EndDate",
            System.Data.SqlDbType.DateTime).Value =
            endDate;


        await using var reader =
            await cmd.ExecuteReaderAsync();


        decimal totalSar = 0;
        decimal totalUsd = 0;


        while (await reader.ReadAsync())
        {
            DateTime expenseDate =
                Convert.ToDateTime(
                    reader["ExpenseDate"]);

            string category =
                reader["Category"]?.ToString()
                ?? "Other";

            string description =
                reader["Description"]?.ToString()
                ?? "";

            decimal amount =
                Convert.ToDecimal(
                    reader["Amount"]);

            string currency =
                reader["Currency"]?.ToString()
                ?? "SAR";

            string paymentMethod =
                reader["PaymentMethod"]?.ToString()
                ?? "Cash";


            if (currency.Equals(
                    "USD",
                    StringComparison.OrdinalIgnoreCase))
            {
                totalUsd += amount;
            }
            else
            {
                totalSar += amount;
            }


            csv.Append(
                CsvEscape(
                    expenseDate.ToString(
                        "yyyy-MM-dd")));

            csv.Append(',');

            csv.Append(
                CsvEscape(category));

            csv.Append(',');

            csv.Append(
                CsvEscape(description));

            csv.Append(',');

            csv.Append(
                amount.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture));

            csv.Append(',');

            csv.Append(
                CsvEscape(currency));

            csv.Append(',');

            csv.Append(
                CsvEscape(paymentMethod));

            csv.AppendLine();
        }


        // --------------------------------------------------------
        // Totals
        // --------------------------------------------------------

        csv.AppendLine();

        csv.AppendLine(
            "TOTAL SAR,,,,," +
            totalSar.ToString(
                "0.00",
                CultureInfo.InvariantCulture));

        csv.AppendLine(
            "TOTAL USD,,,,," +
            totalUsd.ToString(
                "0.00",
                CultureInfo.InvariantCulture));


        string fileName =
            $"Financial_Analysis_{year}_{month:00}.csv";


        byte[] bytes =
            Encoding.UTF8.GetBytes(
                csv.ToString());


        return Results.File(
            bytes,
            "text/csv; charset=utf-8",
            fileName);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Error downloading financial CSV.");

        return Results.Problem(
            "Unable to generate the financial CSV.");
    }
});


// ============================================================
// CSV ESCAPE HELPER
// ============================================================

string CsvEscape(string? value)
{
    if (string.IsNullOrEmpty(value))
    {
        return "";
    }

    if (
        value.Contains(',') ||
        value.Contains('"') ||
        value.Contains('\r') ||
        value.Contains('\n'))
    {
        return "\"" +
               value.Replace(
                   "\"",
                   "\"\"") +
               "\"";
    }

    return value;
}


// ============================================================
// INSERT EXPENSE
// ============================================================

app.MapPost("/insert", async (HttpRequest request) =>
{
    try
    {
        var form =
            await request.ReadFormAsync();


        string expenseDateText =
            form["ExpenseDate"].ToString().Trim();

        string category =
            form["Category"].ToString().Trim();

        string description =
            form["Description"].ToString().Trim();

        string amountText =
            form["Amount"].ToString().Trim();

        string currency =
            form["Currency"].ToString().Trim();

        string paymentMethod =
            form["PaymentMethod"].ToString().Trim();


        // --------------------------------------------------------
        // Validation
        // --------------------------------------------------------

        if (!DateTime.TryParse(
                expenseDateText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime expenseDate))
        {
            return Results.BadRequest(
                "Invalid expense date.");
        }


        if (string.IsNullOrWhiteSpace(category))
        {
            return Results.BadRequest(
                "Category is required.");
        }


        if (!decimal.TryParse(
                amountText,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal amount))
        {
            return Results.BadRequest(
                "Invalid amount.");
        }


        if (amount <= 0)
        {
            return Results.BadRequest(
                "Amount must be greater than zero.");
        }


        if (
            currency != "SAR" &&
            currency != "USD")
        {
            return Results.BadRequest(
                "Invalid currency.");
        }


        if (string.IsNullOrWhiteSpace(
                paymentMethod))
        {
            return Results.BadRequest(
                "Payment method is required.");
        }


        // --------------------------------------------------------
        // Insert
        // --------------------------------------------------------

        string query = $@"
            INSERT INTO [dbo].[{tableName}]
            (
                [ExpenseDate],
                [Category],
                [Description],
                [Amount],
                [Currency],
                [PaymentMethod],
                [CreatedAt]
            )

            VALUES
            (
                @ED,
                @CAT,
                @DESC,
                @AMT,
                @CUR,
                @PM,
                @CR
            );";


        await using var conn =
            CreateConnection();

        await conn.OpenAsync();


        await using var cmd =
            new SqlCommand(
                query,
                conn);


        cmd.Parameters.Add(
            "@ED",
            System.Data.SqlDbType.DateTime).Value =
            expenseDate;


        cmd.Parameters.Add(
            "@CAT",
            System.Data.SqlDbType.NVarChar,
            100).Value =
            category;


        cmd.Parameters.Add(
            "@DESC",
            System.Data.SqlDbType.NVarChar,
            500).Value =
            string.IsNullOrWhiteSpace(
                description)
                ? DBNull.Value
                : description;


        var amountParameter =
            cmd.Parameters.Add(
                "@AMT",
                System.Data.SqlDbType.Decimal);

        amountParameter.Precision = 18;
        amountParameter.Scale = 2;
        amountParameter.Value = amount;


        cmd.Parameters.Add(
            "@CUR",
            System.Data.SqlDbType.NVarChar,
            10).Value =
            currency;


        cmd.Parameters.Add(
            "@PM",
            System.Data.SqlDbType.NVarChar,
            100).Value =
            paymentMethod;


        cmd.Parameters.Add(
            "@CR",
            System.Data.SqlDbType.DateTime).Value =
            DateTime.Now;


        await cmd.ExecuteNonQueryAsync();


        // --------------------------------------------------------
        // Success page
        // --------------------------------------------------------

        const string successHtml = """
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8">

            <meta name="viewport"
                  content="width=device-width, initial-scale=1">

            <title>Expense Saved</title>

            <style>

                body {
                    font-family:
                        Segoe UI,
                        Arial,
                        sans-serif;

                    background: #f4f6f9;

                    text-align: center;

                    padding: 60px 20px;
                }

                .box {
                    background: white;

                    max-width: 450px;

                    margin: auto;

                    padding: 35px;

                    border-radius: 10px;

                    box-shadow:
                        0 4px 15px
                        rgba(0,0,0,.08);
                }

                h2 {
                    color: #28a745;
                }

                a {
                    display: inline-block;

                    margin: 10px;

                    padding: 10px 18px;

                    border-radius: 5px;

                    color: white;

                    text-decoration: none;

                    background: #528B8B;
                }

            </style>

        </head>

        <body>

            <div class="box">

                <h2>
                    ✓ Expense Saved Successfully
                </h2>

                <p>
                    The expense has been added
                    to the database.
                </p>

                <a href="/report">
                    View Report
                </a>

                <a href="/">
                    Add Another
                </a>

            </div>

        </body>
        </html>
        """;


        return Results.Content(
            successHtml,
            "text/html");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Error inserting expense.");

        string safeErrorMessage =
            System.Net.WebUtility.HtmlEncode(
                ex.Message);


        string errorHtml =
            "<!DOCTYPE html>" +
            "<html>" +

            "<head>" +

            "<meta charset=\"utf-8\">" +

            "<meta name=\"viewport\" " +
            "content=\"width=device-width, initial-scale=1\">" +

            "<title>Database Error</title>" +

            "<style>" +

            "body {" +
            "font-family: Segoe UI, Arial, sans-serif;" +
            "background: #f4f6f9;" +
            "padding: 40px 20px;" +
            "}" +

            ".box {" +
            "background: white;" +
            "max-width: 650px;" +
            "margin: auto;" +
            "padding: 30px;" +
            "border-radius: 10px;" +
            "box-shadow: 0 4px 15px rgba(0,0,0,.08);" +
            "}" +

            ".error {" +
            "color: #b42318;" +
            "background: #fef3f2;" +
            "padding: 15px;" +
            "border-radius: 5px;" +
            "word-break: break-word;" +
            "}" +

            "a {" +
            "display: inline-block;" +
            "margin-top: 20px;" +
            "padding: 10px 18px;" +
            "background: #528B8B;" +
            "color: white;" +
            "text-decoration: none;" +
            "border-radius: 5px;" +
            "}" +

            "</style>" +

            "</head>" +

            "<body>" +

            "<div class=\"box\">" +

            "<h2>Database Error</h2>" +

            "<p>" +
            "The expense could not be saved." +
            "</p>" +

            "<div class=\"error\">" +
            safeErrorMessage +
            "</div>" +

            "<a href=\"/\">" +
            "Go Back" +
            "</a>" +

            "</div>" +

            "</body>" +

            "</html>";


        return Results.Content(
            errorHtml,
            "text/html",
            statusCode: 500);
    }
});


app.Run();
