﻿using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Fluent;
using ScalableIPC.Core.Helpers;
using ScalableIPC.IntegrationTests.Helpers;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

// Picked from: https://github.com/xunit/samples.xunit/blob/main/AssemblyFixtureExample/Samples.cs
[assembly: Xunit.TestFramework("ScalableIPC.IntegrationTests.TestAssemblyEntryPoint", "ScalableIPC.IntegrationTests")]

namespace ScalableIPC.IntegrationTests
{
    class TestAssemblyEntryPoint
    {
        public static TestConfiguration Config { get; set; }

        public TestAssemblyEntryPoint()
        {
            try
            {
                IConfigurationRoot config = new ConfigurationBuilder()
                    .AddJsonFile(path: "appsettings.json").Build();
                NLog.Extensions.Logging.ConfigSettingLayoutRenderer.DefaultConfiguration = config;
                Config = config.Get<TestConfiguration>();
                CustomLoggerFacade.Logger = new TestLogger();
            }
            catch (Exception ex)
            {
                var errorTime = DateTime.Now;
                File.AppendAllText($"logs/{errorTime.ToString("yyyy-MM-dd")}.log",
                    $"{errorTime} Failed to initialize test project {ex}\n");
                Environment.Exit(1);
            }
        }

        internal static void ResetDb()
        {
            using (SqlConnection conn = new SqlConnection(Config.ConnectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand("DELETE FROM Logs", conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        internal static List<TestLogRecord> GetTestLogs(Func<TestLogRecord, bool> validateAndFilter)
        {
            return AccessDb(dbConn =>
            {
                return dbConn.Query<TestLogRecord>("SELECT * FROM Logs ORDER BY Id")
                    .Where(validateAndFilter).ToList();
            });
        }

        internal static T AccessDb<T>(Func<IDbConnection, T> dbProc)
        {
            using (IDbConnection conn = new SqlConnection(Config.ConnectionString))
            {
                conn.Open();
                return dbProc.Invoke(conn);
            }
        }
    }

    class TestLogger : ICustomLogger
    {
        public bool LogEnabled => true;
        public bool TestLogEnabled => true;

        public void Log(CustomLogEvent logEvent)
        {
            Logger logger4Evt = logEvent.TargetLogger != null ?
                LogManager.GetLogger(logEvent.TargetLogger) :
                LogManager.GetCurrentClassLogger();
            var logBuilder = logger4Evt.Warn()
                .Message(logEvent.Message ?? "")
                .Exception(logEvent.Error);
            var allProps = JObject.FromObject(logEvent.Data ?? new Dictionary<string, object>());
            logBuilder.Property("logData", allProps.ToString(Formatting.None));
            logBuilder.Write();
        }

        public void TestLog(CustomLogEvent logEvent)
        {
            Logger logger4TestEvt = LogManager.GetLogger(Assembly.GetExecutingAssembly().GetName().Name);
            var logBuilder = logger4TestEvt.Info()
                .Message(logEvent.Message ?? "")
                .Exception(logEvent.Error);
            var allProps = JObject.FromObject(logEvent.Data ?? new Dictionary<string, object>());
            logBuilder.Property("logData", allProps.ToString(Formatting.None));
            logBuilder.Property("targetLogger", logEvent.TargetLogger ?? logger4TestEvt.Name);
            logBuilder.Write();
        }
    }
}