﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace EFCore.BulkExtensions
{
    static class BatchUtil
    {
        // In comment are Examples of how SqlQuery is changed for Sql Batch

        // SELECT [a].[Column1], [a].[Column2], .../r/n
        // FROM [Table] AS [a]/r/n
        // WHERE [a].[Column] = FilterValue
        // --
        // DELETE [a]
        // FROM [Table] AS [a]
        // WHERE [a].[Columns] = FilterValues
        public static string GetSqlDelete<T>(IQueryable<T> query) where T : class, new()
        {
            (string sql, string tableAlias) = GetBatchSql(query);
            return $"DELETE [{tableAlias}]{sql}";
        }

        // SELECT [a].[Column1], [a].[Column2], .../r/n
        // FROM [Table] AS [a]/r/n
        // WHERE [a].[Column] = FilterValue
        // --
        // UPDATE [a] SET [UpdateColumns] = N'updateValues'
        // FROM [Table] AS [a]
        // WHERE [a].[Columns] = FilterValues
        public static string GetSqlUpdate<T>(IQueryable<T> query, DbContext context, T updateValues, List<string> updateColumns) where T : class, new()
        {
            (string sql, string tableAlias) = GetBatchSql(query);
            string sqlSET = GetSqlSetSegment(context, updateValues, updateColumns);
            return $"UPDATE [{tableAlias}] {sqlSET}{sql}";
        }

        public static (string, string) GetBatchSql<T>(IQueryable<T> query) where T : class, new()
        {
            string sqlQuery = query.ToSql();
            string tableAlias = sqlQuery.Substring(8, sqlQuery.IndexOf("]") - 8);
            int indexFROM = sqlQuery.IndexOf(Environment.NewLine);
            string sql = sqlQuery.Substring(indexFROM, sqlQuery.Length - indexFROM);
            return (sql, tableAlias);
        }

        public static string GetSqlSetSegment<T>(DbContext context, T updateValues, List<string> updateColumns) where T : class, new()
        {
            var tableInfo = TableInfo.CreateInstance<T>(context, new List<T>(), OperationType.Read, new BulkConfig());
            string sql = string.Empty;
            Type updateValuesType = typeof(T);
            var defaultValues = new T();
            foreach (var propertyColumn in tableInfo.PropertyColumnNamesDict)
            {
                var property = updateValuesType.GetProperty(propertyColumn.Key);
                string prefixN = property.PropertyType.Name == nameof(String) ? "N" : "";
                var propertyUpdateValue = property.GetValue(updateValues);
                var propertyDefaultValue = property.GetValue(defaultValues);
                bool isDifferentFromDefault = propertyUpdateValue?.ToString() != propertyDefaultValue?.ToString();
                if (isDifferentFromDefault || (updateColumns != null && updateColumns.Contains(propertyColumn.Key)))
                {
                    sql += $"[{ propertyColumn.Value}] = {prefixN}'{propertyUpdateValue}', ";
                }
            }
            if (String.IsNullOrEmpty(sql))
            {
                throw new InvalidOperationException("SET Columns not defined. If one or more columns should be updated to theirs default value use 'updateColumns' argument.");
            }
            sql = sql.Remove(sql.Length - 2, 2); // removes last excess comma and space: ", "
            return $"SET {sql}";
        }

        public static DbContext GetDbContext(IQueryable query)
        {
            var bindingFlags = BindingFlags.NonPublic | BindingFlags.Instance;
            var queryCompiler = typeof(EntityQueryProvider).GetField("_queryCompiler", bindingFlags).GetValue(query.Provider);
            var queryContextFactory = queryCompiler.GetType().GetField("_queryContextFactory", bindingFlags).GetValue(queryCompiler);

            var dependencies = typeof(RelationalQueryContextFactory).GetProperty("Dependencies", bindingFlags).GetValue(queryContextFactory);
            var queryContextDependencies = typeof(DbContext).Assembly.GetType(typeof(QueryContextDependencies).FullName);
            var stateManagerProperty = queryContextDependencies.GetProperty("StateManager", bindingFlags | BindingFlags.Public).GetValue(dependencies);
            var stateManager = (IStateManager)stateManagerProperty;

            return stateManager.Context;
        }
    }
}