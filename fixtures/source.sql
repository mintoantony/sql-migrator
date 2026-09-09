IF DB_ID('SqlMigratorDemo_Source') IS NOT NULL
BEGIN
    ALTER DATABASE [SqlMigratorDemo_Source] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [SqlMigratorDemo_Source];
END
GO
CREATE DATABASE [SqlMigratorDemo_Source];
GO
USE [SqlMigratorDemo_Source];
GO
CREATE TABLE dbo.Customer (
    CustomerId  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
    FirstName   nvarchar(100) NOT NULL,
    LastName    nvarchar(100) NOT NULL,
    Email       nvarchar(200) NULL,
    Description nvarchar(400) NULL,
    IsActive    bit           NOT NULL,
    CreatedUtc  datetime2     NOT NULL
);
CREATE TABLE dbo.[Order] (
    OrderId    int       IDENTITY(1,1) NOT NULL CONSTRAINT PK_Order PRIMARY KEY,
    CustomerId int       NOT NULL CONSTRAINT FK_Order_Customer REFERENCES dbo.Customer(CustomerId),
    OrderDate  datetime2 NOT NULL,
    Total      decimal(18,2) NOT NULL
);
CREATE TABLE dbo.OrderLine (
    OrderLineId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderLine PRIMARY KEY,
    OrderId     int NOT NULL CONSTRAINT FK_OrderLine_Order REFERENCES dbo.[Order](OrderId),
    Product     nvarchar(100) NOT NULL,
    Qty         int NOT NULL,
    UnitPrice   decimal(18,2) NOT NULL
);
GO
INSERT INTO dbo.Customer (FirstName, LastName, Email, Description, IsActive, CreatedUtc)
SELECT TOP (200)
    CONCAT('First', ROW_NUMBER() OVER (ORDER BY (SELECT NULL))),
    CONCAT('Last',  ROW_NUMBER() OVER (ORDER BY (SELECT NULL))),
    CONCAT('user',  ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), '@example.com'),
    REPLICATE(N'd', 300),
    CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 5 = 0 THEN 0 ELSE 1 END,
    DATEADD(day, -ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), CAST('2026-01-01' AS datetime2))
FROM sys.all_objects;
GO
INSERT INTO dbo.[Order] (CustomerId, OrderDate, Total)
SELECT c.CustomerId, DATEADD(day, n.n, CAST('2026-02-01' AS datetime2)), 10.00 * n.n
FROM dbo.Customer c
CROSS JOIN (SELECT TOP (3) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects) n;
GO
INSERT INTO dbo.OrderLine (OrderId, Product, Qty, UnitPrice)
SELECT o.OrderId, CONCAT('Product ', n.n), n.n, 5.50 * n.n
FROM dbo.[Order] o
CROSS JOIN (SELECT TOP (2) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects) n;
GO
