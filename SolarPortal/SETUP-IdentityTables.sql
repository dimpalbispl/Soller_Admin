/* =====================================================================
   SolarPortal — Identity Tables Setup (FINAL)
   =====================================================================
   IMPORTANT: This code renames Identity tables (Users, Roles, etc. — 
   not AspNetUsers). Table names MUST match what DbContext expects.
   ===================================================================== */

USE solfitenergy;
GO

PRINT 'Current database: ' + DB_NAME();
PRINT '';

IF DB_NAME() <> 'solfitenergy'
BEGIN
    PRINT 'STOP! Wrong database. Select solfitenergy from top dropdown.';
    RETURN;
END
GO

-- ════════════════════════════════════════════════════════════════════
-- Drop old tables (both AspNet* and short names — clean slate)
-- ════════════════════════════════════════════════════════════════════
IF OBJECT_ID('dbo.UserTokens', 'U') IS NOT NULL DROP TABLE dbo.UserTokens;
IF OBJECT_ID('dbo.UserRoles', 'U') IS NOT NULL DROP TABLE dbo.UserRoles;
IF OBJECT_ID('dbo.UserLogins', 'U') IS NOT NULL DROP TABLE dbo.UserLogins;
IF OBJECT_ID('dbo.UserClaims', 'U') IS NOT NULL DROP TABLE dbo.UserClaims;
IF OBJECT_ID('dbo.RoleClaims', 'U') IS NOT NULL DROP TABLE dbo.RoleClaims;
IF OBJECT_ID('dbo.Users', 'U') IS NOT NULL DROP TABLE dbo.Users;
IF OBJECT_ID('dbo.Roles', 'U') IS NOT NULL DROP TABLE dbo.Roles;

IF OBJECT_ID('dbo.AspNetUserTokens', 'U') IS NOT NULL DROP TABLE dbo.AspNetUserTokens;
IF OBJECT_ID('dbo.AspNetUserRoles', 'U') IS NOT NULL DROP TABLE dbo.AspNetUserRoles;
IF OBJECT_ID('dbo.AspNetUserLogins', 'U') IS NOT NULL DROP TABLE dbo.AspNetUserLogins;
IF OBJECT_ID('dbo.AspNetUserClaims', 'U') IS NOT NULL DROP TABLE dbo.AspNetUserClaims;
IF OBJECT_ID('dbo.AspNetRoleClaims', 'U') IS NOT NULL DROP TABLE dbo.AspNetRoleClaims;
IF OBJECT_ID('dbo.AspNetUsers', 'U') IS NOT NULL DROP TABLE dbo.AspNetUsers;
IF OBJECT_ID('dbo.AspNetRoles', 'U') IS NOT NULL DROP TABLE dbo.AspNetRoles;

PRINT 'Old tables dropped.';
GO

-- ════════════════════════════════════════════════════════════════════
-- Roles
-- ════════════════════════════════════════════════════════════════════
CREATE TABLE [dbo].[Roles](
    [Id] NVARCHAR(450) NOT NULL,
    [Name] NVARCHAR(256) NULL,
    [NormalizedName] NVARCHAR(256) NULL,
    [ConcurrencyStamp] NVARCHAR(MAX) NULL,
    CONSTRAINT [PK_Roles] PRIMARY KEY ([Id])
);
PRINT 'Roles created';
GO

-- ════════════════════════════════════════════════════════════════════
-- Users — with ALL columns including custom ApplicationUser fields
-- ════════════════════════════════════════════════════════════════════
CREATE TABLE [dbo].[Users](
    [Id] NVARCHAR(450) NOT NULL,
    [UserName] NVARCHAR(256) NULL,
    [NormalizedUserName] NVARCHAR(256) NULL,
    [Email] NVARCHAR(256) NULL,
    [NormalizedEmail] NVARCHAR(256) NULL,
    [EmailConfirmed] BIT NOT NULL DEFAULT 0,
    [PasswordHash] NVARCHAR(MAX) NULL,
    [SecurityStamp] NVARCHAR(MAX) NULL,
    [ConcurrencyStamp] NVARCHAR(MAX) NULL,
    [PhoneNumber] NVARCHAR(MAX) NULL,
    [PhoneNumberConfirmed] BIT NOT NULL DEFAULT 0,
    [TwoFactorEnabled] BIT NOT NULL DEFAULT 0,
    [LockoutEnd] DATETIMEOFFSET NULL,
    [LockoutEnabled] BIT NOT NULL DEFAULT 0,
    [AccessFailedCount] INT NOT NULL DEFAULT 0,
    [FullName] NVARCHAR(MAX) NULL,
    [IsActive] BIT NOT NULL DEFAULT 1,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [UpdatedAt] DATETIME2 NULL,
    [AadharNumber] NVARCHAR(MAX) NULL,
    [PANNumber] NVARCHAR(MAX) NULL,
    [FatherName] NVARCHAR(MAX) NULL,
    [Address] NVARCHAR(MAX) NULL,
    [City] NVARCHAR(MAX) NULL,
    [State] NVARCHAR(MAX) NULL,
    [PinCode] NVARCHAR(MAX) NULL,
    [MobileNumber] NVARCHAR(MAX) NULL,
    CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
);
PRINT 'Users created';
GO

-- ════════════════════════════════════════════════════════════════════
CREATE TABLE [dbo].[RoleClaims](
    [Id] INT IDENTITY(1,1) NOT NULL,
    [RoleId] NVARCHAR(450) NOT NULL,
    [ClaimType] NVARCHAR(MAX) NULL,
    [ClaimValue] NVARCHAR(MAX) NULL,
    CONSTRAINT [PK_RoleClaims] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RoleClaims_Roles_RoleId]
        FOREIGN KEY ([RoleId]) REFERENCES [dbo].[Roles]([Id]) ON DELETE CASCADE
);
PRINT 'RoleClaims created';
GO

CREATE TABLE [dbo].[UserClaims](
    [Id] INT IDENTITY(1,1) NOT NULL,
    [UserId] NVARCHAR(450) NOT NULL,
    [ClaimType] NVARCHAR(MAX) NULL,
    [ClaimValue] NVARCHAR(MAX) NULL,
    CONSTRAINT [PK_UserClaims] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_UserClaims_Users_UserId]
        FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users]([Id]) ON DELETE CASCADE
);
PRINT 'UserClaims created';
GO

CREATE TABLE [dbo].[UserLogins](
    [LoginProvider] NVARCHAR(450) NOT NULL,
    [ProviderKey] NVARCHAR(450) NOT NULL,
    [ProviderDisplayName] NVARCHAR(MAX) NULL,
    [UserId] NVARCHAR(450) NOT NULL,
    CONSTRAINT [PK_UserLogins] PRIMARY KEY ([LoginProvider], [ProviderKey]),
    CONSTRAINT [FK_UserLogins_Users_UserId]
        FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users]([Id]) ON DELETE CASCADE
);
PRINT 'UserLogins created';
GO

CREATE TABLE [dbo].[UserRoles](
    [UserId] NVARCHAR(450) NOT NULL,
    [RoleId] NVARCHAR(450) NOT NULL,
    CONSTRAINT [PK_UserRoles] PRIMARY KEY ([UserId], [RoleId]),
    CONSTRAINT [FK_UserRoles_Users_UserId]
        FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users]([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_UserRoles_Roles_RoleId]
        FOREIGN KEY ([RoleId]) REFERENCES [dbo].[Roles]([Id]) ON DELETE CASCADE
);
PRINT 'UserRoles created';
GO

CREATE TABLE [dbo].[UserTokens](
    [UserId] NVARCHAR(450) NOT NULL,
    [LoginProvider] NVARCHAR(450) NOT NULL,
    [Name] NVARCHAR(450) NOT NULL,
    [Value] NVARCHAR(MAX) NULL,
    CONSTRAINT [PK_UserTokens] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
    CONSTRAINT [FK_UserTokens_Users_UserId]
        FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users]([Id]) ON DELETE CASCADE
);
PRINT 'UserTokens created';
GO

-- Verify
PRINT '';
PRINT '=== Identity tables ===';
SELECT TABLE_NAME 
FROM INFORMATION_SCHEMA.TABLES 
WHERE TABLE_NAME IN ('Users','Roles','UserRoles','UserClaims','UserLogins','UserTokens','RoleClaims')
ORDER BY TABLE_NAME;
GO
