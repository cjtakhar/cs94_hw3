using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HW3NoteKeeper.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Check if the 'Note' table exists and create it if it doesn't
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Note')
                BEGIN
                    CREATE TABLE [Note] (
                        [NoteId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                        [Summary] NVARCHAR(60) NOT NULL,
                        [Details] NVARCHAR(1024) NOT NULL,
                        [CreatedDateUtc] DATETIME2 NOT NULL,
                        [ModifiedDateUtc] DATETIME2
                    );
                END");

            // Check if the 'Tag' table exists and create it if it doesn't
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Tag')
                BEGIN
                    CREATE TABLE [Tag] (
                        [Id] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                        [NoteId] UNIQUEIDENTIFIER NOT NULL,
                        [Name] NVARCHAR(30) NOT NULL,
                        FOREIGN KEY ([NoteId]) REFERENCES [Note]([NoteId]) ON DELETE CASCADE
                    );
                END");

            // Check if the index on 'Tag.NoteId' exists, and create it if it doesn't
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('Tag') AND name = 'IX_Tag_NoteId')
                BEGIN
                    CREATE INDEX IX_Tag_NoteId ON [Tag] ([NoteId]);
                END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop 'Tag' table if it exists
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Tag')
                BEGIN
                    DROP TABLE [Tag];
                END");

            // Drop 'Note' table if it exists
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Note')
                BEGIN
                    DROP TABLE [Note];
                END");
        }
    }
}
