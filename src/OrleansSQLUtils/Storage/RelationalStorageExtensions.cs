﻿/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.SqlUtils
{
    /// <summary>
    /// Convenienience functions to work with objects of type <see cref="IRelationalStorage"/>.
    /// </summary>
    public static class RelationalStorageExtensions
    {
        /// <summary>
        /// Used to format .NET objects suitable to relational database format.
        /// </summary>
        private static readonly SqlFormatProvider sqlFormatProvider = new SqlFormatProvider();

        /// <summary>
        /// This is a template to produce query parameters that are indexed.
        /// </summary>
        private static readonly string indexedParameterTemplate = "@p{0}";

        /// <summary>
        /// This is used to acquire some constants that change rarely if ever.
        /// </summary>
        private static readonly QueryConstantsBag queryConstants = new QueryConstantsBag();


        /// <summary>
        /// Executes a multi-record insert query clause with <em>SELECT UNION ALL</em>.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="storage">The storage to use.</param>
        /// <param name="tableName">The table name to against which to execute the query.</param>
        /// <param name="parameters">The parameters to insert.</param>
        /// <param name="nameMap">If provided, maps property names from <typeparamref name="T"/> to ones provided in the map.</param>
        /// <param name="onlyOnceColumns">If given, SQL parameter values for the given <typeparamref name="T"/> property types are generated only once. Effective only when <paramref name="useSqlParams"/> is <em>TRUE</em>.</param>
        /// <param name="useSqlParams"><em>TRUE</em> if the query should be in parameterized form. <em>FALSE</em> otherwise.</param>
        /// <returns>The rows affected.</returns>
        public static Task<int> ExecuteMultipleInsertIntoAsync<T>(this IRelationalStorage storage, string tableName, IEnumerable<T> parameters, IReadOnlyDictionary<string, string> nameMap = null, IEnumerable<string> onlyOnceColumns = null, bool useSqlParams = true)
        {
            if(string.IsNullOrWhiteSpace(tableName))
            {
                throw new ArgumentException("The name must be a legal SQL table name", "tableName");
            }

            if(parameters == null)
            {
                throw new ArgumentNullException("parameters");
            }

            var startEscapeIndicator = queryConstants.GetConstant(storage.InvariantName, RelationalVendorConstants.StartEscapeIndicatorKey);
            var endEscapeIndicator = queryConstants.GetConstant(storage.InvariantName, RelationalVendorConstants.EndEscapeIndicatorKey);

            //SqlParameters map is needed in case the query needs to be parameterized in order to avoid two
            //reflection passes as first a query needs to be constructed and after that when a database
            //command object has been created, parameters need to be provided to them.
            var sqlParameters = new Dictionary<string, object>();
            const string insertIntoValuesTemplate = "INSERT INTO {0} ({1}) SELECT {2};";
            var columns = string.Empty;
            var values = new List<string>();
            if(parameters.Any())
            {
                //Type and property information are the same for all of the objects.
                //The following assumes the property names will be retrieved in the same
                //order as is the index iteration done.                                
                var onlyOnceRow = new List<string>();
                var properties = parameters.First().GetType().GetProperties();
                columns = string.Join(",", nameMap == null ? properties.Select(pn => string.Format("{0}{1}{2}", startEscapeIndicator, pn.Name, endEscapeIndicator)) : properties.Select(pn => string.Format("{0}{1}{2}", startEscapeIndicator, (nameMap.ContainsKey(pn.Name) ? nameMap[pn.Name] : pn.Name), endEscapeIndicator)));
                if(onlyOnceColumns != null && onlyOnceColumns.Any())
                {
                    var onlyOnceProperties = properties.Where(pn => onlyOnceColumns.Contains(pn.Name)).Select(pn => pn).ToArray();
                    var onlyOnceData = parameters.First();
                    for(int i = 0; i < onlyOnceProperties.Length; ++i)
                    {
                        var currentProperty = onlyOnceProperties[i];
                        var parameterValue = currentProperty.GetValue(onlyOnceData, null);
                        if(useSqlParams)
                        {
                            var parameterName = string.Format("@{0}", (nameMap.ContainsKey(onlyOnceProperties[i].Name) ? nameMap[onlyOnceProperties[i].Name] : onlyOnceProperties[i].Name));
                            onlyOnceRow.Add(parameterName);
                            sqlParameters.Add(parameterName, parameterValue);
                        }
                        else
                        {
                            onlyOnceRow.Add(string.Format(sqlFormatProvider, "{0}", parameterValue));
                        }
                    }
                }

                var dataRows = new List<string>();
                var multiProperties = onlyOnceColumns == null ? properties : properties.Where(pn => !onlyOnceColumns.Contains(pn.Name)).Select(pn => pn).ToArray();
                int parameterCount = 0;
                foreach(var row in parameters)
                {
                    for(int i = 0; i < multiProperties.Length; ++i)
                    {
                        var currentProperty = multiProperties[i];
                        var parameterValue = currentProperty.GetValue(row, null);
                        if(useSqlParams)
                        {
                            var parameterName = string.Format(indexedParameterTemplate, parameterCount);
                            dataRows.Add(parameterName);
                            sqlParameters.Add(parameterName, parameterValue);
                            ++parameterCount;
                        }
                        else
                        {
                            dataRows.Add(string.Format(sqlFormatProvider, "{0}", parameterValue));
                        }
                    }

                    values.Add(string.Format("{0}", string.Join(",", onlyOnceRow.Concat(dataRows))));
                    dataRows.Clear();
                }
            }

            //If this is an Oracle database, every UNION ALL SELECT needs to have "FROM DUAL" appended.
            if(storage.InvariantName == AdoNetInvariants.InvariantNameOracleDatabase)
            {
                //Counting starts from 1 as the first SELECT should not select from dual.
                for(int i = 1; i < values.Count; ++i)
                {
                    values[i] = string.Concat(values[i], " FROM DUAL");
                }
            }

            var query = string.Format(insertIntoValuesTemplate, tableName, columns, string.Join(" UNION ALL SELECT ", values));
            return storage.ExecuteAsync(query, command =>
            {
                if(useSqlParams)
                {
                    foreach(var sp in sqlParameters)
                    {
                        var p = command.CreateParameter();
                        p.ParameterName = sp.Key;
                        p.Value = sp.Value ?? DBNull.Value;
                        p.Direction = ParameterDirection.Input;
                        command.Parameters.Add(p);
                    }
                }
            });
        }


        /// <summary>
        /// Uses <see cref="IRelationalStorage"/> with <see cref="DbExtensions.ReflectionParameterProvider{T}(IDbCommand, T, IReadOnlyDictionary{string, string})"/>.
        /// </summary>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>SELECT</em> statement, but works with other queries too.</param>
        /// <param name="parameters">Adds parameters to the query. Parameter names must match those defined in the query.</param>
        /// <returns>A list of objects as a result of the <see paramref="query"/>.</returns>
        /// <example>This uses reflection to read results and match the parameters.
        /// <code>
        /// //This struct holds the return value in this example.        
        /// public struct Information
        /// {
        ///     public string TABLE_CATALOG { get; set; }
        ///     public string TABLE_NAME { get; set; }
        /// }
        /// 
        /// //Here reflection (<seealso cref="DbExtensions.ReflectionParameterProvider{T}(IDbCommand, T, IReadOnlyDictionary{string, string})"/>)
        /// is used to match parameter names as well as to read back the results (<seealso cref="DbExtensions.ReflectionSelector{TResult}(IDataRecord)"/>).
        /// var query = "SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @tname;";
        /// IEnumerable&lt;Information&gt; informationData = await db.ReadAsync&lt;Information&gt;(query, new { tname = 200000 });
        /// </code>
        /// </example>
        public static async Task<IEnumerable<TResult>> ReadAsync<TResult>(this IRelationalStorage storage, string query, object parameters)
        {
            return await storage.ReadAsync(query, command =>
            {
                if(parameters != null)
                {
                    command.ReflectionParameterProvider(parameters);
                }
            }, (selector, resultSetCount) => selector.ReflectionSelector<TResult>()).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Uses <see cref="IRelationalStorage"/> with <see cref="DbExtensions.ReflectionParameterProvider{T}(System.Data.IDbCommand, T, IReadOnlyDictionary{string, string})">DbExtensions.ReflectionParameterProvider</see>.
        /// </summary>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>SELECT</em> statement, but works with other queries too.</param>        
        /// <returns>A list of objects as a result of the <see paramref="query"/>.</returns>
        public static async Task<IEnumerable<TResult>> ReadAsync<TResult>(this IRelationalStorage storage, string query)
        {
            return await ReadAsync<TResult>(storage, query, null).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Uses <see cref="IRelationalStorage"/> with <see cref="DbExtensions.ReflectionSelector{TResult}(System.Data.IDataRecord)"/>.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>INSERT</em>, <em>UPDATE</em>, <em>DELETE</em> or <em>DDL</em> queries.</param>
        /// <param name="parameters">Adds parameters to the query. Parameter names must match those defined in the query.</param>
        /// <returns>Affected rows count.</returns>
        /// <example>This uses reflection to provide parameters to an execute
        /// query that reads only affected rows count if available.
        /// <code>        
        /// //Here reflection (<seealso cref="DbExtensions.ReflectionParameterProvider{T}(IDbCommand, T, IReadOnlyDictionary{string, string})"/>)
        /// is used to match parameter names as well as to read back the results (<seealso cref="DbExtensions.ReflectionSelector{TResult}(IDataRecord)"/>).
        /// var query = "IF NOT EXISTS(SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @tname) CREATE TABLE Test(Id INT PRIMARY KEY IDENTITY(1, 1) NOT NULL);"
        /// await db.ExecuteAsync(query, new { tname = "test_table" });
        /// </code>
        /// </example>
        public static async Task<int> ExecuteAsync(this IRelationalStorage storage, string query, object parameters)
        {
            return await storage.ExecuteAsync(query, command =>
            {
                if(parameters != null)
                {
                    command.ReflectionParameterProvider(parameters);
                }
            }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Uses <see cref="IRelationalStorage"/> with <see cref="DbExtensions.ReflectionSelector{TResult}(System.Data.IDataRecord)"/>.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>INSERT</em>, <em>UPDATE</em>, <em>DELETE</em> or <em>DDL</em> queries.</param>        
        /// <returns>Affected rows count.</returns>
        public static async Task<int> ExecuteAsync(this IRelationalStorage storage, string query)
        {
            return await ExecuteAsync(storage, query, null).ConfigureAwait(continueOnCapturedContext: false);
        }        
    }
}
