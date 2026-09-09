IF DB_ID('SqlMigratorDemo_Target') IS NOT NULL
BEGIN
    ALTER DATABASE [SqlMigratorDemo_Target] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [SqlMigratorDemo_Target];
END
GO
CREATE DATABASE [SqlMigratorDemo_Target];
GO
USE [SqlMigratorDemo_Target];
GO
CREATE TABLE dbo.Client (
    ClientId     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Client PRIMARY KEY,
    FullName     nvarchar(200) NOT NULL,
    ShortName    nvarchar(20)  NOT NULL,
    EmailAddress nvarchar(200) NULL,
    EmailDomain  nvarchar(100) NULL,
    Summary      nvarchar(100) NULL,
    Active       bit           NOT NULL,
    CreatedUtc   datetime2     NOT NULL,
    MigratedUtc  datetime2     NOT NULL,
    Notes        nvarchar(500) NULL
);
CREATE TABLE dbo.[Order] (
    OrderId   int       IDENTITY(1,1) NOT NULL CONSTRAINT PK_Order PRIMARY KEY,
    ClientId  int       NOT NULL CONSTRAINT FK_Order_Client REFERENCES dbo.Client(ClientId),
    OrderDate datetime2 NOT NULL,
    Total     decimal(18,2) NOT NULL
);
CREATE TABLE dbo.OrderLine (
    OrderLineId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderLine PRIMARY KEY,
    OrderId     int NOT NULL CONSTRAINT FK_OrderLine_Order REFERENCES dbo.[Order](OrderId),
    ProductName nvarchar(100) NOT NULL,
    Quantity    int NOT NULL,
    UnitPrice   decimal(18,2) NOT NULL
);
GO
