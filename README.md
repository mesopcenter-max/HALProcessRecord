# HAL Ring Rolling Process Record - ASP.NET Core

This project creates an ASP.NET Core MVC web application that asks the user to enter process-record values and then fills the supplied Excel template without rebuilding the template's formatting.

## Technology

- .NET 8 ASP.NET Core MVC
- SQL Server + Entity Framework Core 8
- ClosedXML for Excel generation
- Existing workbook used as the master template
- Razor Pages with .cshtml extensions

## Project structure

```text
HALProcessRecord/
├── Controllers/
│   └── ProcessRecordController.cs
├── Data/
│   └── AppDbContext.cs
├── Models/
│   ├── ProcessRecordEntity.cs
│   └── OperationRecordEntity.cs
├── Services/
│   ├── IExcelGeneratorService.cs
│   └── ExcelGeneratorService.cs
├── ViewModels/
│   └── ProcessRecordViewModel.cs
├── Views/
│   ├── ProcessRecord/
│   │   ├── Create.cshtml
│   │   └── History.cshtml
│   ├── Shared/
│   │   ├── _Layout.cshtml
│   │   └── _ValidationScriptsPartial.cshtml
│   ├── _ViewImports.cshtml
│   └── _ViewStart.cshtml
├── wwwroot/css/site.css
├── Templates/
│   └── HAL_Ring_Rolling_Process_Record_GE1.xlsx
├── appsettings.json
├── appsettings.Development.json
├── HALProcessRecord.csproj
└── Program.cs
```

## Run

1. Install .NET 8 SDK.
2. Install SQL Server or use an existing SQL Server instance.
3. Change `DefaultConnection` in `appsettings.json`.
4. Open the `.csproj` in Visual Studio.
5. Restore NuGet packages.
6. Run the application.
7. Open `/ProcessRecord/Create`.
8. Enter the values.
9. Click **Generate Excel**.

The application creates the `HALProcessRecord` database automatically on first run using `EnsureCreated()`.

## Excel mapping from the supplied template

### Basic fields

- D9 = Date
- D10 = Part No
- D11 = Qty
- D12 = Batch No
- D13 = Loading Date
- D14 = Loading Time
- J9 = Furnace
- J10 = Forging Temperature
- J11 = Temperature Tolerance
- J12 = Soaking Start Temperature
- J13 = Soaking Hours Min
- J14 = Soaking Hours Max
- D16 = Soaking Start Time
- D17 = Minimum Soaking Complete
- D18 = Operation to End Before
- J16 = Actual Operation Start Time
- J17 = Actual Operation End Time
- J18 = Operator Name
- D25 = Ring Rolling Stage No
- D26 = Mandrel Diameter
- D44 = Other Remarks
- A46 = Prepared / Recorded By
- H46 = Verified By

### Check marks

Operation:
- F21 = Upset & Pierce
- F22 = Axial Pressing
- F23 = Flattening

Equipment:
- M21 = 1500 Ton Press
- M22 = 3000 Ton Press
- M23 = 4000 Ton Press

### Operation table

Rows 30 through 42 are the 13 rows available in the original template:

- C = Transfer minutes
- D = Transfer seconds
- E = Total minutes
- F = Total seconds
- G = Calculated operation minutes
- H = Calculated operation seconds
- I = Observation / Remarks

Operation Time is calculated as:

`Total Time - Transfer Time`

## Important

The workbook in `Templates` is the master template. Do not redesign it in HTML or C#. If the customer changes the Excel format later, replace the template and update only the cell mapping in `ExcelGeneratorService.cs` if the cell positions changed.

## Future Opcenter integration

The recommended next phase is to add an MES service/API layer:

```text
Opcenter MES
    ↓
MES API / Integration Layer
    ↓
ASP.NET Core
    ↓
Pre-populate Work Order / Part / Batch / Operation / Equipment
    ↓
Operator enters actual values
    ↓
SQL Server
    ↓
Excel template generation
```

Do not connect the UI directly to the Opcenter database. Use the supported Opcenter integration/API mechanism where available, and keep the Excel generator independent.
