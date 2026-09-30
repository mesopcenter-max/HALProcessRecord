using Microsoft.EntityFrameworkCore;

namespace HALProcessRecord.Data;

public static class TimingDatabaseInitializer
{
    public static void Initialize(AppDbContext db)
    {
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("""
            IF OBJECT_ID(N'[MachineTimingStates]', N'U') IS NULL
            BEGIN
                CREATE TABLE [MachineTimingStates]
                (
                    [Id] int NOT NULL IDENTITY,
                    [Furnace] nvarchar(20) NOT NULL,
                    [Machine] nvarchar(50) NOT NULL,
                    [LastDoorOpen] bit NULL,
                    [LastDoorClosed] bit NULL,
                    [HalfDoorCloseTransitions] int NOT NULL,
                    [HalfDoor] bit NOT NULL,
                    [LastAutoStartLED] bit NULL,
                    [LastAutoStartPB] bit NULL,
                    [LastAutoStopLED] bit NULL,
                    [LastAutoStopPB] bit NULL,
                    [LastMaterialForce] decimal(18,2) NOT NULL,
                    [TransferStartAt] datetime2 NULL,
                    [TransferEndAt] datetime2 NULL,
                    [TotalStartAt] datetime2 NULL,
                    [StartMode] nvarchar(max) NULL,
                    [LastUpdatedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_MachineTimingStates] PRIMARY KEY ([Id])
                );
                CREATE UNIQUE INDEX [IX_MachineTimingStates_Furnace_Machine]
                    ON [MachineTimingStates] ([Furnace], [Machine]);
            END;

            IF COL_LENGTH(N'[ProcessRecords]', N'Machine') IS NULL
            BEGIN
                ALTER TABLE [ProcessRecords] ADD [Machine] nvarchar(50) NOT NULL CONSTRAINT [DF_ProcessRecords_Machine] DEFAULT N'MRB_3000';
            END;

            IF COL_LENGTH(N'[MachineTimingStates]', N'TransferEndAt') IS NULL
            BEGIN
                ALTER TABLE [MachineTimingStates] ADD [TransferEndAt] datetime2 NULL;
            END;

            IF COL_LENGTH(N'[MachineTimingStates]', N'LastDoorClosed') IS NULL
                ALTER TABLE [MachineTimingStates] ADD [LastDoorClosed] bit NULL;
            IF COL_LENGTH(N'[MachineTimingStates]', N'HalfDoorCloseTransitions') IS NULL
                ALTER TABLE [MachineTimingStates] ADD [HalfDoorCloseTransitions] int NOT NULL CONSTRAINT [DF_MachineTimingStates_HalfDoorCloseTransitions] DEFAULT 0;
            IF COL_LENGTH(N'[MachineTimingStates]', N'HalfDoor') IS NULL
                ALTER TABLE [MachineTimingStates] ADD [HalfDoor] bit NOT NULL CONSTRAINT [DF_MachineTimingStates_HalfDoor] DEFAULT 0;

            IF COL_LENGTH(N'[OperationRecords]', N'FurnaceDoorOpenTime') IS NULL
                ALTER TABLE [OperationRecords] ADD [FurnaceDoorOpenTime] datetime2 NULL;
            IF COL_LENGTH(N'[OperationRecords]', N'FurnaceDoorCloseTime') IS NULL
                ALTER TABLE [OperationRecords] ADD [FurnaceDoorCloseTime] datetime2 NULL;
            IF COL_LENGTH(N'[OperationRecords]', N'AutoStartTime') IS NULL
                ALTER TABLE [OperationRecords] ADD [AutoStartTime] datetime2 NULL;
            IF COL_LENGTH(N'[OperationRecords]', N'StopTime') IS NULL
                ALTER TABLE [OperationRecords] ADD [StopTime] datetime2 NULL;
            IF COL_LENGTH(N'[OperationRecords]', N'Temperature') IS NULL
                ALTER TABLE [OperationRecords] ADD [Temperature] decimal(18,2) NULL;
            IF COL_LENGTH(N'[OperationRecords]', N'HighestTemperature') IS NULL
                ALTER TABLE [OperationRecords] ADD [HighestTemperature] decimal(18,2) NULL;

            IF OBJECT_ID(N'[TimingTransferIntervals]', N'U') IS NULL
            BEGIN
                CREATE TABLE [TimingTransferIntervals]
                (
                    [Id] bigint NOT NULL IDENTITY,
                    [Furnace] nvarchar(20) NOT NULL,
                    [Machine] nvarchar(50) NOT NULL,
                    [OpenAt] datetime2 NOT NULL,
                    [CloseAt] datetime2 NULL,
                    [MachineStartAt] datetime2 NULL,
                    [MachineStopAt] datetime2 NULL,
                    [TimingCycleId] int NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_TimingTransferIntervals] PRIMARY KEY ([Id])
                );
                CREATE INDEX [IX_TimingTransferIntervals_Furnace_Machine_OpenAt]
                    ON [TimingTransferIntervals] ([Furnace], [Machine], [OpenAt]);
            END;

            IF OBJECT_ID(N'[TimingCycles]', N'U') IS NULL
            BEGIN
                CREATE TABLE [TimingCycles]
                (
                    [Id] int NOT NULL IDENTITY,
                    [Furnace] nvarchar(20) NOT NULL,
                    [Machine] nvarchar(50) NOT NULL,
                    [StartMode] nvarchar(20) NOT NULL,
                    [TransferStartAt] datetime2 NULL,
                    [TotalStartAt] datetime2 NOT NULL,
                    [StoppedAt] datetime2 NOT NULL,
                    [TransferSeconds] int NULL,
                    [TotalSeconds] int NOT NULL,
                    [OperationSeconds] int NOT NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_TimingCycles] PRIMARY KEY ([Id])
                );
            END;

            IF OBJECT_ID(N'[TimingCycles]', N'U') IS NOT NULL
               AND NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = N'IX_TimingCycles_Furnace_Machine_TotalStartAt_StoppedAt'
                      AND object_id = OBJECT_ID(N'[TimingCycles]')
               )
            BEGIN
                CREATE INDEX [IX_TimingCycles_Furnace_Machine_TotalStartAt_StoppedAt]
                    ON [TimingCycles] ([Furnace], [Machine], [TotalStartAt], [StoppedAt]);
            END;

            IF OBJECT_ID(N'[FurnaceData]', N'U') IS NULL
            BEGIN
                CREATE TABLE [FurnaceData]
                (
                    [Id] bigint NOT NULL IDENTITY,
                    [Furnace] nvarchar(20) NOT NULL,
                    [DoorOpen] bit NOT NULL,
                    [DoorClosed] bit NOT NULL,
                    [HalfDoor] bit NOT NULL,
                    [Code] nvarchar(100) NOT NULL,
                    [DoorOpenSourceTimestamp] datetime2 NULL,
                    [DoorClosedSourceTimestamp] datetime2 NULL,
                    [SourceReadAt] datetime2 NOT NULL,
                    [CapturedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_FurnaceData] PRIMARY KEY ([Id])
                );
                CREATE INDEX [IX_FurnaceData_Furnace_CapturedAt]
                    ON [FurnaceData] ([Furnace], [CapturedAt]);
            END;

            IF COL_LENGTH(N'[FurnaceData]', N'HalfDoor') IS NULL
                ALTER TABLE [FurnaceData] ADD [HalfDoor] bit NOT NULL CONSTRAINT [DF_FurnaceData_HalfDoor] DEFAULT 0;

            IF OBJECT_ID(N'[MachineData]', N'U') IS NULL
            BEGIN
                CREATE TABLE [MachineData]
                (
                    [Id] bigint NOT NULL IDENTITY,
                    [Machine] nvarchar(50) NOT NULL,
                    [AutoStartLed] bit NOT NULL,
                    [AutoStopLed] bit NOT NULL,
                    [AutoStartPushButton] bit NOT NULL,
                    [AutoStopPushButton] bit NOT NULL,
                    [MaterialForce] decimal(18,2) NOT NULL,
                    [MaterialTemperature] decimal(18,2) NOT NULL,
                    [AutoStartLedSourceTimestamp] datetime2 NULL,
                    [AutoStopLedSourceTimestamp] datetime2 NULL,
                    [AutoStartPushButtonSourceTimestamp] datetime2 NULL,
                    [AutoStopPushButtonSourceTimestamp] datetime2 NULL,
                    [SourceReadAt] datetime2 NOT NULL,
                    [CapturedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_MachineData] PRIMARY KEY ([Id])
                );
                CREATE INDEX [IX_MachineData_Machine_CapturedAt]
                    ON [MachineData] ([Machine], [CapturedAt]);
            END;

            UPDATE [FurnaceData]
            SET [SourceReadAt] = [CapturedAt]
            WHERE [SourceReadAt] < '2020-01-01';

            UPDATE [FurnaceData]
            SET [DoorOpenSourceTimestamp] = [CapturedAt]
            WHERE [DoorOpenSourceTimestamp] < '2020-01-01';

            UPDATE [FurnaceData]
            SET [DoorClosedSourceTimestamp] = [CapturedAt]
            WHERE [DoorClosedSourceTimestamp] < '2020-01-01';

            UPDATE [MachineData]
            SET [SourceReadAt] = [CapturedAt]
            WHERE [SourceReadAt] < '2020-01-01';

            UPDATE [MachineData]
            SET [AutoStartLedSourceTimestamp] = [CapturedAt]
            WHERE [AutoStartLedSourceTimestamp] < '2020-01-01';

            UPDATE [MachineData]
            SET [AutoStopLedSourceTimestamp] = [CapturedAt]
            WHERE [AutoStopLedSourceTimestamp] < '2020-01-01';

            UPDATE [MachineData]
            SET [AutoStartPushButtonSourceTimestamp] = [CapturedAt]
            WHERE [AutoStartPushButtonSourceTimestamp] < '2020-01-01';

            UPDATE [MachineData]
            SET [AutoStopPushButtonSourceTimestamp] = [CapturedAt]
            WHERE [AutoStopPushButtonSourceTimestamp] < '2020-01-01';

            UPDATE [TimingCycles]
            SET [StoppedAt] = [CreatedAt],
                [TotalStartAt] = DATEADD(SECOND, -[TotalSeconds], [CreatedAt]),
                [TransferStartAt] = CASE WHEN [TransferSeconds] IS NULL THEN NULL
                    ELSE DATEADD(SECOND, -[TransferSeconds], DATEADD(SECOND, -[TotalSeconds], [CreatedAt])) END
            WHERE [TotalStartAt] < '2020-01-01' OR [StoppedAt] < '2020-01-01';

            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @MigrationLockResult int;
            EXEC @MigrationLockResult = sp_getapplock
                @Resource = N'HALProcessRecord.TimestampStorage.IstV1',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction';

            IF @MigrationLockResult < 0
                THROW 50001, 'Could not acquire the timestamp migration lock.', 1;

            IF OBJECT_ID(N'[TimestampStorageMigrations]', N'U') IS NULL
            BEGIN
                CREATE TABLE [TimestampStorageMigrations]
                (
                    [Name] nvarchar(100) NOT NULL PRIMARY KEY,
                    [AppliedAt] datetime2 NOT NULL
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM [TimestampStorageMigrations] WHERE [Name] = N'UtcToIstWallClockV1')
            BEGIN
                UPDATE [FurnaceData] SET
                    [DoorOpenSourceTimestamp] = DATEADD(MINUTE, 330, [DoorOpenSourceTimestamp]),
                    [DoorClosedSourceTimestamp] = DATEADD(MINUTE, 330, [DoorClosedSourceTimestamp]),
                    [SourceReadAt] = DATEADD(MINUTE, 330, [SourceReadAt]),
                    [CapturedAt] = DATEADD(MINUTE, 330, [CapturedAt]);

                UPDATE [MachineData] SET
                    [AutoStartLedSourceTimestamp] = DATEADD(MINUTE, 330, [AutoStartLedSourceTimestamp]),
                    [AutoStopLedSourceTimestamp] = DATEADD(MINUTE, 330, [AutoStopLedSourceTimestamp]),
                    [AutoStartPushButtonSourceTimestamp] = DATEADD(MINUTE, 330, [AutoStartPushButtonSourceTimestamp]),
                    [AutoStopPushButtonSourceTimestamp] = DATEADD(MINUTE, 330, [AutoStopPushButtonSourceTimestamp]),
                    [SourceReadAt] = DATEADD(MINUTE, 330, [SourceReadAt]),
                    [CapturedAt] = DATEADD(MINUTE, 330, [CapturedAt]);

                UPDATE [MachineTimingStates] SET
                    [TransferStartAt] = DATEADD(MINUTE, 330, [TransferStartAt]),
                    [TransferEndAt] = DATEADD(MINUTE, 330, [TransferEndAt]),
                    [TotalStartAt] = DATEADD(MINUTE, 330, [TotalStartAt]),
                    [LastUpdatedAt] = DATEADD(MINUTE, 330, [LastUpdatedAt]);

                UPDATE [TimingTransferIntervals] SET
                    [OpenAt] = DATEADD(MINUTE, 330, [OpenAt]),
                    [CloseAt] = DATEADD(MINUTE, 330, [CloseAt]),
                    [MachineStartAt] = DATEADD(MINUTE, 330, [MachineStartAt]),
                    [MachineStopAt] = DATEADD(MINUTE, 330, [MachineStopAt]),
                    [CreatedAt] = DATEADD(MINUTE, 330, [CreatedAt]);

                UPDATE [TimingCycles] SET
                    [TransferStartAt] = DATEADD(MINUTE, 330, [TransferStartAt]),
                    [TotalStartAt] = DATEADD(MINUTE, 330, [TotalStartAt]),
                    [StoppedAt] = DATEADD(MINUTE, 330, [StoppedAt]),
                    [CreatedAt] = DATEADD(MINUTE, 330, [CreatedAt]);

                INSERT INTO [TimestampStorageMigrations] ([Name], [AppliedAt])
                VALUES (N'UtcToIstWallClockV1', DATEADD(MINUTE, 330, SYSUTCDATETIME()));
            END;

            COMMIT TRANSACTION;
            """);
    }
}
