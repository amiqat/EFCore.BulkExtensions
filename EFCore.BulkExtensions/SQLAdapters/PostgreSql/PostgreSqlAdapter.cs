﻿using EFCore.BulkExtensions.Helpers;
using EFCore.BulkExtensions.SqlAdapters;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EFCore.BulkExtensions.SQLAdapters.PostgreSql
{
    public class PostgreSqlAdapter : ISqlOperationsAdapter
    {
        #region Methods
        // Insert
        public void Insert<T>(DbContext context, Type type, IList<T> entities, TableInfo tableInfo, Action<decimal> progress)
        {
            InsertAsync(context, type, entities, tableInfo, progress, CancellationToken.None, isAsync: false).GetAwaiter().GetResult();
        }

        public async Task InsertAsync<T>(DbContext context, Type type, IList<T> entities, TableInfo tableInfo, Action<decimal> progress, CancellationToken cancellationToken)
        {
            await InsertAsync(context, type, entities, tableInfo, progress, cancellationToken, isAsync: true).ConfigureAwait(false);
        }
        protected async Task InsertAsync<T>(DbContext context, Type type, IList<T> entities, TableInfo tableInfo, Action<decimal> progress, CancellationToken cancellationToken, bool isAsync)
        {
            NpgsqlConnection connection = tableInfo.NpgsqlConnection;
            bool closeConnectionInternally = false;
            if (connection == null)
            {
                (connection, closeConnectionInternally) =
                    isAsync ? await OpenAndGetNpgsqlConnectionAsync(context, tableInfo.BulkConfig, cancellationToken).ConfigureAwait(false)
                            : OpenAndGetNpgsqlConnection(context, tableInfo.BulkConfig);
            }

            try
            {
                string sqlCopy = SqlQueryBuilderPostgreSql.InsertIntoTable(tableInfo, tableInfo.InsertToTempTable ? OperationType.InsertOrUpdate : OperationType.Insert);

                using var writer = isAsync ? await connection.BeginBinaryImportAsync(sqlCopy, cancellationToken).ConfigureAwait(false)
                                           : connection.BeginBinaryImport(sqlCopy);

                var uniqueColumnName = tableInfo.PrimaryKeysPropertyColumnNameDict.Values.ToList().FirstOrDefault();

                var doKeepIdentity = tableInfo.BulkConfig.SqlBulkCopyOptions == SqlBulkCopyOptions.KeepIdentity;
                var propertiesColumnDict = ((tableInfo.InsertToTempTable || doKeepIdentity) && tableInfo.IdentityColumnName == uniqueColumnName)
                    ? tableInfo.PropertyColumnNamesDict
                    : tableInfo.PropertyColumnNamesDict.Where(a => a.Value != tableInfo.IdentityColumnName);

                var propertiesNames = propertiesColumnDict.Select(a => a.Key).ToList();
                var entitiesCopiedCount = 0;
                foreach (var entity in entities)
                {
                    if (isAsync)
                    {
                        await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        writer.StartRow();
                    }

                    foreach (var propertyName in propertiesNames)
                    {
                        if (tableInfo.DefaultValueProperties.Contains(propertyName) && !tableInfo.PrimaryKeysPropertyColumnNameDict.ContainsKey(propertyName))
                            continue;

                        var propertyValue = tableInfo.FastPropertyDict.ContainsKey(propertyName) ? tableInfo.FastPropertyDict[propertyName].Get(entity) : null;
                        var propertyColumnName = tableInfo.PropertyColumnNamesDict.ContainsKey(propertyName) ? tableInfo.PropertyColumnNamesDict[propertyName] : string.Empty;

                        var columnType = tableInfo.ColumnNamesTypesDict[propertyColumnName];

                        // string is 'text' which works fine
                        if (columnType.StartsWith("character")) // when MaxLength is defined: 'character(1)' or 'character varying'
                            columnType = "character"; // 'character' is like 'string'
                        else if (columnType.StartsWith("varchar"))
                            columnType = "varchar";
                        else if (columnType.StartsWith("numeric"))
                            columnType = "numeric";

                        var convertibleDict = tableInfo.ConvertibleColumnConverterDict;
                        if (convertibleDict.TryGetValue(propertyColumnName, out var converter))
                        {
                            if (propertyValue != null)
                            {
                                if (converter.ModelClrType.IsEnum)
                                {
                                    var clrType = converter.ProviderClrType;
                                    if (clrType == typeof(byte)) // columnType == "smallint"
                                        propertyValue = (byte)propertyValue;
                                    if (clrType == typeof(short))
                                        propertyValue = (short)propertyValue;
                                    if (clrType == typeof(Int32))
                                        propertyValue = (int)propertyValue;
                                    if (clrType == typeof(Int64))
                                        propertyValue = (long)propertyValue;
                                    if (clrType == typeof(string))
                                        propertyValue = propertyValue.ToString();
                                }
                                else
                                {
                                    propertyValue = converter.ConvertToProvider.Invoke(propertyValue);
                                }
                            }
                        }

                        if (isAsync)
                        {
                            await writer.WriteAsync(propertyValue, columnType, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            writer.Write(propertyValue, columnType);
                        }
                    }
                    entitiesCopiedCount++;
                    if (progress != null && entitiesCopiedCount % tableInfo.BulkConfig.NotifyAfter == 0)
                    {
                        progress?.Invoke(ProgressHelper.GetProgress(entities.Count, entitiesCopiedCount));
                    }
                }
                if (isAsync)
                {
                    await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    writer.Complete();
                }
            }
            finally
            {
                if (closeConnectionInternally)
                {
                    //connection.Close();
                    if (isAsync)
                    {
                        await connection.CloseAsync();
                        //await context.Database.CloseConnectionAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        connection.Close();
                        //context.Database.CloseConnection();
                    }
                }
            }
        }

        // Merge
        public void Merge<T>(DbContext context, Type type, IList<T> entities, TableInfo tableInfo, OperationType operationType, Action<decimal> progress) where T : class
        {
            MergeAsync(context, type, entities, tableInfo, operationType, progress, CancellationToken.None, isAsync: false).GetAwaiter().GetResult();
        }

        public async Task MergeAsync<T>(DbContext context, Type type, IList<T> entities, TableInfo tableInfo, OperationType operationType, Action<decimal> progress, CancellationToken cancellationToken) where T : class
        {
            await MergeAsync(context, type, entities, tableInfo, operationType, progress, cancellationToken, isAsync: true).ConfigureAwait(false);
        }

        protected async Task MergeAsync<T>(DbContext context, Type type, IList<T> entities, TableInfo tableInfo, OperationType operationType, Action<decimal> progress, CancellationToken cancellationToken, bool isAsync) where T : class
        {
            var entityPropertyWithDefaultValue = entities.GetPropertiesWithDefaultValue(type);

            if (tableInfo.BulkConfig.CustomSourceTableName == null)
            {
                tableInfo.InsertToTempTable = true;

                var sqlCreateTableCopy = SqlQueryBuilderPostgreSql.CreateTableCopy(tableInfo.FullTableName, tableInfo.FullTempTableName, tableInfo);
                if (isAsync)
                {
                    await context.Database.ExecuteSqlRawAsync(sqlCreateTableCopy, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    context.Database.ExecuteSqlRaw(sqlCreateTableCopy);
                }
            }

            bool hasUniqueConstrain = await CheckHasExplicitUniqueConstrainAsync(context, tableInfo, cancellationToken, isAsync);
            if (hasUniqueConstrain == false)
            {
                if (tableInfo.EntityPKPropertyColumnNameDict == tableInfo.PrimaryKeysPropertyColumnNameDict)
                {
                    hasUniqueConstrain = true; // ExplicitUniqueConstrain not required for PK
                }
            }
            bool doDropUniqueConstrain = false;

            try
            {
                if (tableInfo.BulkConfig.CustomSourceTableName == null)
                {
                    if (isAsync)
                    {
                        await InsertAsync(context, type, entities, tableInfo, progress, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        Insert(context, type, entities, tableInfo, progress);
                    }
                }

                if (!hasUniqueConstrain)
                {
                    string createUniqueIndex = SqlQueryBuilderPostgreSql.CreateUniqueIndex(tableInfo);
                    string createUniqueConstrain = SqlQueryBuilderPostgreSql.CreateUniqueConstrain(tableInfo);
                    if (isAsync)
                    {
                        await context.Database.ExecuteSqlRawAsync(createUniqueIndex, cancellationToken).ConfigureAwait(false);
                        await context.Database.ExecuteSqlRawAsync(createUniqueConstrain, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        context.Database.ExecuteSqlRaw(createUniqueIndex);
                        context.Database.ExecuteSqlRaw(createUniqueConstrain);
                    }
                    doDropUniqueConstrain = true;
                }

                var sqlMergeTable = SqlQueryBuilderPostgreSql.MergeTable<T>(context, tableInfo, operationType, entityPropertyWithDefaultValue);
                if (operationType != OperationType.Read && (!tableInfo.BulkConfig.SetOutputIdentity || operationType == OperationType.Delete))
                {
                    if (isAsync)
                    {
                        await context.Database.ExecuteSqlRawAsync(sqlMergeTable, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        context.Database.ExecuteSqlRaw(sqlMergeTable);
                    }
                }
                else
                {
                    List<T> outputEntities = tableInfo.LoadOutputEntities<T>(context, type, sqlMergeTable);
                    tableInfo.UpdateReadEntities(type, entities, outputEntities, context);
                }
            }
            finally
            {
                if (doDropUniqueConstrain)
                {
                    string dropUniqueConstrain = SqlQueryBuilderPostgreSql.DropUniqueConstrain(tableInfo);
                    if (isAsync)
                    {
                        await context.Database.ExecuteSqlRawAsync(dropUniqueConstrain, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        context.Database.ExecuteSqlRaw(dropUniqueConstrain);
                    }
                }

                if (!tableInfo.BulkConfig.UseTempDB)
                {
                    if (tableInfo.BulkConfig.CustomSourceTableName == null)
                    {
                        var sqlDropTable = SqlQueryBuilderPostgreSql.DropTable(tableInfo.FullTempTableName, tableInfo.BulkConfig.UseTempDB);
                        if (isAsync)
                        {
                            await context.Database.ExecuteSqlRawAsync(sqlDropTable, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            context.Database.ExecuteSqlRaw(sqlDropTable);
                        }
                    }
                }
            }
        }

        // Read
        public void Read<T>(DbContext context, Type type, IList<T> entities, TableInfo tableInfo, Action<decimal> progress) where T : class
        {
            ReadAsync(context, type, entities, tableInfo, progress, CancellationToken.None, isAsync: false).GetAwaiter().GetResult();
        }

        public async Task ReadAsync<T>(DbContext context, Type type, IList<T> entities, TableInfo tableInfo, Action<decimal> progress, CancellationToken cancellationToken) where T : class
        {
            await ReadAsync(context, type, entities, tableInfo, progress, cancellationToken, isAsync: true).ConfigureAwait(false);
        }

        protected async Task ReadAsync<T>(DbContext context, Type type, IList<T> entities, TableInfo tableInfo, Action<decimal> progress, CancellationToken cancellationToken, bool isAsync) where T : class
        {
            await MergeAsync(context, type, entities, tableInfo, OperationType.Read, progress, cancellationToken, isAsync);
        }

        // Truncate
        public void Truncate(DbContext context, TableInfo tableInfo)
        {
            var sqlTruncateTable = SqlQueryBuilderPostgreSql.TruncateTable(tableInfo.FullTableName);
            context.Database.ExecuteSqlRaw(sqlTruncateTable);
        }

        public async Task TruncateAsync(DbContext context, TableInfo tableInfo, CancellationToken cancellationToken)
        {
            var sqlTruncateTable = SqlQueryBuilderPostgreSql.TruncateTable(tableInfo.FullTableName);
            await context.Database.ExecuteSqlRawAsync(sqlTruncateTable, cancellationToken).ConfigureAwait(false);
        }
        #endregion

        #region Connection
        internal static async Task<(NpgsqlConnection, bool)> OpenAndGetNpgsqlConnectionAsync(DbContext context, BulkConfig bulkConfig, CancellationToken cancellationToken)
        {
            bool closeConnectionInternally = false;
            var npgsqlConnection = (NpgsqlConnection)context.Database.GetDbConnection();
            if (npgsqlConnection.State != ConnectionState.Open)
            {
                await npgsqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                closeConnectionInternally = true;
            }
            return (npgsqlConnection, closeConnectionInternally);

            //await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            //return (NpgsqlConnection)context.Database.GetDbConnection();
        }

        internal static (NpgsqlConnection, bool) OpenAndGetNpgsqlConnection(DbContext context, BulkConfig bulkConfig)
        {
            bool closeConnectionInternally = false;
            var npgsqlConnection = (NpgsqlConnection)context.Database.GetDbConnection();
            if (npgsqlConnection.State != ConnectionState.Open)
            {
                npgsqlConnection.Open();
                closeConnectionInternally = true;
            }
            return (npgsqlConnection, closeConnectionInternally);

            //context.Database.OpenConnection();
            //return (NpgsqlConnection)context.Database.GetDbConnection();

        }
        #endregion

        internal async Task<bool> CheckHasExplicitUniqueConstrainAsync(DbContext context, TableInfo tableInfo, CancellationToken cancellationToken, bool isAsync)
        {
            string countUniqueConstrain = SqlQueryBuilderPostgreSql.CountUniqueConstrain(tableInfo);

            bool hasUniqueConstrain = false;
            using (var command = context.Database.GetDbConnection().CreateCommand())
            {
                //command.CommandText = @"SELECT COUNT(*) FROM ""Item""";
                //var count = command.ExecuteScalar();

                command.CommandText = countUniqueConstrain;
                context.Database.OpenConnection();

                if (isAsync)
                {
                    using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (reader.HasRows)
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            hasUniqueConstrain = (long)reader[0] == 1;
                        }
                    }
                }
                else
                {
                    using var reader = command.ExecuteReader();
                    if (reader.HasRows)
                    {
                        while (reader.Read())
                        {
                            hasUniqueConstrain = (long)reader[0] == 1;
                        }
                    }
                }
            }
            return hasUniqueConstrain;
        }
    }
}
