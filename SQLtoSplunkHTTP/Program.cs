﻿// Copyright (c) Andrew Robinson. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Data.SqlClient;
using System.Data;
using System.Linq;
using System.Text;
using System.Timers;
using Newtonsoft.Json;
using SQLtoSplunkHTTP.Helpers;
using System.IO;
using System.Net;
using System.Globalization;
using SplunkHTTPUtility;
using Microsoft.Extensions.CommandLineUtils;
using System.Reflection;

[assembly: log4net.Config.XmlConfigurator(ConfigFile = "log.config", Watch = true)]
namespace SQLtoSplunkHTTP
{
    class Program
    {
        #region Globals

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static bool _isExecutingSQLCommand = false;
        private static readonly object _updateisExecutingSQLCommand = new object();

        internal static string GetExecutingDirectoryName()
        {
            var location = new Uri(Assembly.GetEntryAssembly().GetName().CodeBase);
            return new FileInfo(location.AbsolutePath).Directory.FullName;
        }

        // HTTP Client for data transmission to Splunk
        private static SplunkHTTP splunkHTTPClient;
        internal static SplunkHTTP SplunkHTTPClient
        {
            get
            {
                return splunkHTTPClient;
            }

            set
            {
                splunkHTTPClient = value;
            }
        }

        //Setup Timer for reading logs
        private static Timer readTimer;
        public static Timer ReadTimer
        {
            get
            {
                if(readTimer == null)
                {
                    readTimer = new Timer();
                }

                return readTimer;
            }

            set
            {
                readTimer = value;
            }
        }

        //Runtime Options Object
        private static Options runtimeOptions;
        internal static Options RuntimeOptions
        {
            get
            {
                if (runtimeOptions == null)
                {
                    runtimeOptions = ReadOptionsFile(OptionsFilePathOption);
                }

                return runtimeOptions;
            }

            set
            {
                runtimeOptions = value;
            }
        }

        // Global SQL Connection
        private static SqlConnection sqlConnectionObject;
        internal static SqlConnection SQLConnectionObject
        {
            get
            {
                if (sqlConnectionObject == null)
                {
                    log.DebugFormat("Connection String : {0}", RuntimeOptions.SQLConnectionString);
                    sqlConnectionObject = new SqlConnection(RuntimeOptions.SQLConnectionString);
                }

                if (sqlConnectionObject.State != ConnectionState.Open)
                {
                    log.Info("Opening SQL connection");
                    try
                    {
                        sqlConnectionObject.Open();
                    }
                    catch(Exception ex)
                    {
                        log.Error(ex);
                    }
                }

                return sqlConnectionObject;
            }

            set
            {
                sqlConnectionObject = value;
            }
        }

        internal static string CacheFilename
        {
            get
            {
                var directory = GetExecutingDirectoryName();

                if (string.IsNullOrEmpty(RuntimeOptions.CacheFilename))
                {
                    return Path.Combine(directory, RuntimeOptions.SplunkSourceData + "-" + RuntimeOptions.SQLSequenceField + ".txt");
                }
                else
                {
                    return Path.Combine(directory,RuntimeOptions.CacheFilename);
                }
            }
        }
        
        private static CommandOption optionsFilePathOption;
        internal static CommandOption OptionsFilePathOption
        {
            get
            {
                return optionsFilePathOption;
            }

            set
            {
                optionsFilePathOption = value;
            }
        }
        
        #endregion

        static int Main(string[] args)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                log.InfoFormat("Starting {0} Version {1}", assembly.Location, assembly.GetName().Version.ToString());
                
                #region Argument Options
                
                var app = new CommandLineApplication(throwOnUnexpectedArg: false)
                {
                    Name = "SQLToSplunkHTTP",
                    Description = "Command line application meant to forward records from a SQL Server Database to a Splunk HTTP collector",
                    FullName = "SQL Server to Splunk HTTP Collector"      
                };
                
                // Define app Options; 
                app.HelpOption("-?| -h| --help");
                app.VersionOption("-v| --version", assembly.GetName().Version.MajorRevision.ToString(), assembly.GetName().Version.ToString());

                optionsFilePathOption = app.Option("-o| --optionsfile <PATH>", "Path to options file (Optional)", CommandOptionType.SingleValue);
                
        app.OnExecute(() =>
                {
                    // Setup the SplunkHTTPClient
                    SplunkHTTPClient = new SplunkHTTP(log, RuntimeOptions.SplunkAuthorizationToken, RuntimeOptions.SplunkBaseAddress, RuntimeOptions.SplunkClientID);

                    //Eat any SSL errors if configured to do so via options
                    // TODO : Test this feature
                    ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        return RuntimeOptions.SplunkIgnoreSSLErrors;
                    };

                    // Configure Timer
                    ReadTimer.Interval = RuntimeOptions.ReadInterval;
                    
                    // Create delegate to handle elapsed time event
                    ReadTimer.Elapsed += ReadTimer_Elapsed;

                    //Start Timer
                    ReadTimer.Start();

                    //Prevent console from exiting
                    Console.Read();
                    return 0;
                });
               
                app.Command("clearcache", c =>
                 {

                     c.Description = "Deletes the current cache file";
                     c.HelpOption("-?| -h| --help");

                     c.OnExecute(() =>
                     {
                         return ClearCache(CacheFilename);                         
                     });
                 });

                app.Command("createdefaultoptionsfile", c =>
                {
                    c.Description = "Create a default options.json file";
                    c.HelpOption("-?| -h| --help");

                    var overWriteOption = c.Option("-o| --overwrite", "Overwrite file if it exists",CommandOptionType.NoValue);
                    var fileNameOption = c.Option("-f| --filename <PATH>", "Name of options file (Optional)", CommandOptionType.SingleValue);
                    
                    c.OnExecute(() =>
                    {
                        return CreateDefaultOptionsFile(fileNameOption.Value() ?? "options.json", overWriteOption.HasValue());
                    });
                });

                //Debug the startup arguments                
                log.DebugFormat("Startup Arguments {0}",JsonConvert.SerializeObject(args));

                //Always make sure we load runtime options first
                //RuntimeOptions = ReadOptionsFile(OptionsFilePathOption);
                
                // Run the application with arguments
                return app.Execute(args);
                
                #endregion
            }
            catch (Exception ex)
            {
                log.Error(ex);
                return -1;
            }
        }

        /// <summary>
        /// Clear cache file
        /// </summary>
        /// <param name="CacheFileName"></param>
        /// <returns></returns>
        private static int ClearCache(string CacheFileName)
        {
            try
            {
                log.InfoFormat("Deleting cache file {0}", CacheFileName);
                System.IO.File.Delete(CacheFileName);

                return 0;
            }
            catch(Exception ex)
            {
                log.Error(ex);
                return -1;
            }

        }
        
        /// <summary>
        /// Write a default options file to disk
        /// </summary>
        /// <param name="fileName">Filename for the options file</param>
        /// <param name="overWrite">Overwrite an existing file if it exists</param>
        /// <returns></returns>
        private static int CreateDefaultOptionsFile(string fileName = "options.json", bool overWrite = false)
        {
            try
            {
                if (System.IO.File.Exists(fileName))
                {
                    log.InfoFormat("{0} exists", fileName);

                    if (!overWrite)
                    {
                        log.InfoFormat("Applications options not set to overwrite {0}.  Specify options to overwrite or use different filename.", fileName);
                        return 0;
                    }
                    else
                    {
                        log.InfoFormat("Overwriting {0}", fileName);
                    }
                }

                System.IO.File.WriteAllText(fileName, JsonConvert.SerializeObject(new Options(), Formatting.Indented));
                log.InfoFormat("Wrote default options to {0}", fileName);

                return 0;
            }
            catch(Exception ex)
            {
                log.Error(ex);
                return -1;
            }
        }

        /// <summary>
        /// Read an options file and return an Options object
        /// </summary>
        /// <param name="optionsFilePathOption">Path to options file</param>
        /// <returns></returns>
        private static Options ReadOptionsFile(CommandOption optionsFilePathOption)
        {
            try
            {
                var optionsFilename = optionsFilePathOption.Value() ?? "options.json";
                var optionsPath = Path.Combine(GetExecutingDirectoryName(), optionsFilename);

                log.InfoFormat("Using options file {0}", optionsPath);

                if (System.IO.File.Exists(optionsPath))
                {
                    return JsonConvert.DeserializeObject<Options>(System.IO.File.ReadAllText(optionsPath));
                }
                else
                {
                    log.WarnFormat("Specified options file {0} does not exist. Loading default values.", optionsPath);
                    return new Options();
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
                return null;
            }
        }

        private static void ReadTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            ReadAndTransmitData(GetSqlQuery(RuntimeOptions),SQLConnectionObject);
        }

        /// <summary>
        /// Read data and transmit via HTTP to Splunk
        /// </summary>
        /// <param name="query"></param>
        /// <param name="sqlConnectionObject"></param>
        private static void ReadAndTransmitData(string query, SqlConnection sqlConnectionObject)
        {
            DataTable dataTable = new DataTable();
            string kvpValue = "";
            var localisExecutingSQLCommand = false;

            try
            {
                if (sqlConnectionObject.State == ConnectionState.Open)
                {
                    if(string.IsNullOrEmpty(query))
                    {
                        throw new Exception("Query string is null or empty");
                    }
                    
                    lock(_updateisExecutingSQLCommand)
                    {
                        localisExecutingSQLCommand = _isExecutingSQLCommand;
                    }

                    log.DebugFormat("localisExecutingSQLCommand = {0}", localisExecutingSQLCommand);
                    
                    if (!localisExecutingSQLCommand)
                    {
                        SqlCommand command = new SqlCommand(query, sqlConnectionObject);

                        lock (_updateisExecutingSQLCommand)
                        {
                            _isExecutingSQLCommand = true;
                        }

                        dataTable.Load(command.ExecuteReader());

                        lock (_updateisExecutingSQLCommand)
                        {
                            _isExecutingSQLCommand = false;
                        }

                        log.InfoFormat("{0} rows retrieved", dataTable.Rows.Count);

                        if (dataTable.Rows.Count > 0)
                        {
                            //Build the additional KVP values to Append
                            var additionalKVPValues = new StringBuilder();

                            additionalKVPValues.AppendFormat("{0}=\"{1}\", ", "SourceHost", RuntimeOptions.SplunkSourceHost);
                            additionalKVPValues.AppendFormat("{0}=\"{1}\" ", "SourceData", RuntimeOptions.SplunkSourceData);

                            //Get the KVP string for the records
                            kvpValue = dataTable.ToKVP(additionalKVPValues.ToString(), RuntimeOptions.SQLTimestampField, RuntimeOptions.SplunkEventTimestampFormat);

                            log.DebugFormat("KVP Values");
                            log.DebugFormat("{0}", kvpValue);

                            //Transmit the records
                            var result = SplunkHTTPClient.TransmitValues(kvpValue);

                            log.DebugFormat("Transmit Values Result - {0}", result);

                            //If successful then write the last sequence value to disk
                            if (result.StatusCode == HttpStatusCode.OK)
                            {

                                log.DebugFormat("Writing Cache File");

                                // Write the last sequence value to the cache value named for the SQLSequence Field.  Order the result set by the sequence field then select the first record
                                WriteCacheFile(dataTable, CacheFilename, RuntimeOptions);

                                if (ReadTimer.Interval != RuntimeOptions.ReadInterval)
                                {
                                    //Reset timer interval
                                    ClearTimerBackoff(ReadTimer, RuntimeOptions);
                                }
                            }
                            else
                            {
                                // Implement a timer backoff so we don't flood the endpoint
                                IncrementTimerBackoff(ReadTimer, RuntimeOptions);
                                log.WarnFormat("HTTP Transmission not OK - {0}", result);
                            }
                        }

                    }
                    else
                    {
                        log.DebugFormat("SQL command already executing.  Skipping this cycle.");
                    }

                }
                else
                {
                    log.Warn("SQL Connection not open");
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
            finally
            {
                dataTable.Dispose();
            }
        }

        /// <summary>
        /// Write an entry for the maximum sequence field value into the cache file
        /// </summary>
        /// <param name="dataTable">Transmitted records</param>
        /// <param name="cacheFileName">Filename to write cache data to</param>
        /// <param name="runtimeOptions">Runtime Options Object</param>
        private static void WriteCacheFile(DataTable dataTable, string cacheFileName, Options runtimeOptions)
        {
            string cacheWriteValue;

            try
            {
                cacheWriteValue = string.Format("{0:" + runtimeOptions.CacheWriteValueStringFormat + "}", dataTable.AsEnumerable().OrderByDescending(r => r[runtimeOptions.SQLSequenceField]).First()[runtimeOptions.SQLSequenceField]);
                log.DebugFormat("cacheWriteValue : {0}", cacheWriteValue);
                File.WriteAllText(cacheFileName, cacheWriteValue);
            }
            catch(Exception ex)
            {
                log.Error(ex);
            }
        }

        /// <summary>
        /// Calculate SQL query from options and cache values
        /// </summary>
        /// <param name="runtimeOptions">Runtime Options object</param>
        /// <returns>String representing SQL Query based on provided runtime options</returns>
        private static string GetSqlQuery(Options runtimeOptions)
        {
            string query = "";
            string cachedSqlSequenceFieldValue;
            DateTime cachedSqlSequenceFieldValueDateTime;
            DateTimeStyles cacheDateTimeStyle;
            
            // Add the where clause if we can get the cached Sequence Field Value
            try
            {
                query = runtimeOptions.SQLQuery;

                if (string.IsNullOrEmpty(query))
                {
                    throw new Exception("SQL Query in options file is empty or null");
                }

                //Get the base query and limit by TOP XX.  If there is no {{MaxRecords}} component then this statement makes no change to the query
                query = query.Replace("{{MaxRecords}}", runtimeOptions.MaxRecords.ToString());

                if (File.Exists(CacheFilename))
                {
                    cachedSqlSequenceFieldValue = File.ReadAllText(CacheFilename) ?? string.Empty;
                }
                else
                {
                    cachedSqlSequenceFieldValue = runtimeOptions.SQLSequenceFieldDefaultValue;
                }

                if (runtimeOptions.CacheWriteValueIsUTCTimestamp)
                {
                    cacheDateTimeStyle = DateTimeStyles.AssumeUniversal;
                }
                else
                {
                    cacheDateTimeStyle = DateTimeStyles.AssumeLocal;
                }

                if (DateTime.TryParseExact(cachedSqlSequenceFieldValue, runtimeOptions.CacheWriteValueStringFormat, CultureInfo.InvariantCulture, cacheDateTimeStyle, out cachedSqlSequenceFieldValueDateTime))
                {
                    cachedSqlSequenceFieldValue = cachedSqlSequenceFieldValueDateTime.AddMilliseconds(runtimeOptions.CacheWriteValueTimestampMillisecondsAdd).ToString(runtimeOptions.CacheWriteValueStringFormat);
                }

                if (cachedSqlSequenceFieldValue != string.Empty)
                {
                    query += runtimeOptions.SQLWhereClause.Replace("{{SQLSequenceField}}", runtimeOptions.SQLSequenceField).Replace("{{LastSQLSequenceFieldValue}}", cachedSqlSequenceFieldValue);
                }

                //Finally add the Order By Clause
                query += runtimeOptions.SQLOrderByClause.Replace("{{SQLSequenceField}}", runtimeOptions.SQLSequenceField);

                log.DebugFormat("SQL Query : {0}", query);
            }
            catch
            {
                // Do Nothing
            }
            
            return query;
        }

        /// <summary>
        /// Slow down the timer by doubling the interval up to MaximumReadInterval
        /// </summary>
        private static void IncrementTimerBackoff(Timer readTimer, Options runtimeOptions)
        {
            try
            {
                lock (readTimer)
                {
                    var currentInterval = readTimer.Interval;

                    if (currentInterval < runtimeOptions.MaximumReadInterval)
                    {
                        readTimer.Interval = System.Math.Min(currentInterval * 2, runtimeOptions.MaximumReadInterval);
                        log.WarnFormat("Read Timer interval set to {0} milliseconds", readTimer.Interval);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
                // Set to a default read interval of 60000
                readTimer.Interval = 60000;
            }
        }

        /// <summary>
        /// Slow down the timer by doubling the interval up to MaximumReadInterval
        /// </summary>
        private static void ClearTimerBackoff(Timer readTimer, Options runtimeOptions)
        {
            try
            {                
                log.InfoFormat("Restoring transmission timer interval to {0}", runtimeOptions.ReadInterval);
                lock (readTimer)
                {
                    readTimer.Interval = RuntimeOptions.ReadInterval;
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
                // Set to a default read interval of 60000
                readTimer.Interval = 60000;
            }
        }
    }
}