﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WatsonWebserver;
using SyslogLogging;
using Komodo.Core;
using Komodo.Server.Classes;

namespace Komodo.Server
{
    public partial class KomodoServer
    {
        public static string _Version;

        public static Config _Config;
        public static LoggingModule _Logging;
        public static ConnectionManager _Conn;
        public static UserManager _User;
        public static ApiKeyManager _ApiKey;
        public static IndexManager _Index;
        public static ConsoleManager _Console;
        public static WatsonWebserver.Server _Server;
        
        public static void Main(string[] args)
        {
            try
            {
                #region Welcome

                _Version = Assembly.GetExecutingAssembly().GetName().Version.ToString();

                Console.WriteLine(Welcome());

                #endregion

                #region Initial-Setup

                bool initialSetup = false;
                if (args != null && args.Length >= 1)
                {
                    if (String.Compare(args[0], "setup") == 0) initialSetup = true;
                }

                if (!Common.FileExists("System.json")) initialSetup = true;
                if (initialSetup)
                {
                    Setup setup = new Setup();
                }

                #endregion
                
                #region Initialize-Globals

                _Config = Config.FromFile("System.json");

                _Logging = new LoggingModule(
                    _Config.Logging.SyslogServerIp,
                    _Config.Logging.SyslogServerPort,
                    _Config.Logging.ConsoleLogging,
                    (LoggingModule.Severity)_Config.Logging.MinimumLevel,
                    false,
                    true,
                    true,
                    false,
                    false,
                    false);

                _Conn = new ConnectionManager();
                _User = new UserManager(_Logging, UserMaster.FromFile(_Config.Files.UserMaster));
                _ApiKey = new ApiKeyManager(_Logging, ApiKey.FromFile(_Config.Files.ApiKey), ApiKeyPermission.FromFile(_Config.Files.ApiKeyPermission));
                _Index = new IndexManager(_Config.Files.Indices, _Logging);

                _Server = new WatsonWebserver.Server(
                    _Config.Server.ListenerHostname, 
                    _Config.Server.ListenerPort, 
                    _Config.Server.Ssl, 
                    RequestReceived);

                _Server.ContentRoutes.Add("/SearchApp/", true);
                _Server.ContentRoutes.Add("/Assets/", true);
                _Server.AccessControl.Mode = AccessControlMode.DefaultPermit;
                 
                if (_Config.EnableConsole) _Console = new ConsoleManager(_Config, _Index, ExitApplication); 

                #endregion

                #region Wait-for-Server-Thread

                EventWaitHandle waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, Guid.NewGuid().ToString());
                bool waitHandleSignal = false;
                do
                {
                    waitHandleSignal = waitHandle.WaitOne(1000);
                } while (!waitHandleSignal);

                _Logging.Log(LoggingModule.Severity.Debug, "KomodoServer exiting");

                #endregion 
            }
            catch (Exception e)
            {
                LoggingModule.ConsoleException("KomodoServer", "Main", e);
            }
        }

        public static string Welcome()
        {
            string ret =
                Environment.NewLine +
                Environment.NewLine +
                "oooo                                                    .o8            " + Environment.NewLine +
                "`888                                                    888            " + Environment.NewLine +
                " 888  oooo   .ooooo.  ooo. .oo.  .oo.    .ooooo.   .oooo888   .ooooo.  " + Environment.NewLine +
                " 888 .8P'   d88' `88b `888P'Y88bP'Y88b  d88' `88b d88' `888  d88' `88b " + Environment.NewLine +
                " 888888.    888   888  888   888   888  888   888 888   888  888   888 " + Environment.NewLine +
                " 888 `88b.  888   888  888   888   888  888   888 888   888  888   888 " + Environment.NewLine +
                "o888o o888o `Y8bod8P' o888o o888o o888o `Y8bod8P' `Y8bod88P  `Y8bod8P' " + Environment.NewLine +
                Environment.NewLine;

            return ret;
        }
        
        public static HttpResponse RequestReceived(HttpRequest req)
        {
            HttpResponse resp = new HttpResponse(req, 500, null, "application/json",
                Encoding.UTF8.GetBytes(new ErrorResponse(500, "Outer exception.", null).ToJson(true)));

            DateTime startTime = DateTime.Now.ToUniversalTime();

            try
            {
                #region Variables

                string apiKey = "";
                string email = "";
                string password = "";

                UserMaster currUserMaster = null;
                ApiKey currApiKey = null;
                ApiKeyPermission currApiKeyPermission = null;
                RequestMetadata md = new RequestMetadata();

                if (_Config.Logging.LogHttpRequests)
                {
                    _Logging.Log(LoggingModule.Severity.Debug, "RequestReceived request received: " + Environment.NewLine + req.ToString());
                }

                #endregion

                #region Options-Handler

                if (req.Method == HttpMethod.OPTIONS)
                { 
                    resp = OptionsHandler(req);
                    return resp;
                }

                #endregion

                #region Favicon-Robots-Root

                if (req.RawUrlEntries != null && req.RawUrlEntries.Count > 0)
                {
                    if (String.Compare(req.RawUrlEntries[0].ToLower(), "favicon.ico") == 0)
                    {
                        resp = new HttpResponse(req, 200, null, null, null);
                        return resp;
                    }
                }

                if (req.RawUrlEntries != null && req.RawUrlEntries.Count > 0)
                {
                    if (String.Compare(req.RawUrlEntries[0].ToLower(), "robots.txt") == 0)
                    {
                        resp = new HttpResponse(req, 200, null, "text/plain", Encoding.UTF8.GetBytes("User-Agent: *\r\nDisallow:\r\n"));
                        return resp;
                    }
                }

                if (req.RawUrlEntries == null || req.RawUrlEntries.Count == 0)
                { 
                    resp = new HttpResponse(req, 200, null, "text/html", Encoding.UTF8.GetBytes(RootHtml()));
                    return resp;
                }

                #endregion

                #region Add-Connection

                _Conn.Add(Thread.CurrentThread.ManagedThreadId, req);

                #endregion

                #region Unauthenticated-API

                switch (req.Method)
                {
                    case HttpMethod.GET: 
                        if (WatsonCommon.UrlEqual(req.RawUrlWithoutQuery, "/loopback", false))
                        {
                            resp = new HttpResponse(req, 200, null, "text/plain", Encoding.UTF8.GetBytes("Hello from Komodo!"));
                            return resp;
                        } 

                        if (WatsonCommon.UrlEqual(req.RawUrlWithoutQuery, "/version", false))
                        {
                            resp = new HttpResponse(req, 200, null, "text/plain", Encoding.UTF8.GetBytes(_Version));
                            return resp;
                        } 
                        break; 

                    default:
                        break;
                }

                #endregion

                #region Retrieve-Authentication

                apiKey = req.RetrieveHeaderValue(_Config.Server.HeaderApiKey);
                email = req.RetrieveHeaderValue(_Config.Server.HeaderEmail);
                password = req.RetrieveHeaderValue(_Config.Server.HeaderPassword);
                
                #endregion

                #region Admin-API

                if (req.RawUrlEntries != null && req.RawUrlEntries.Count > 0)
                {
                    if (String.Compare(req.RawUrlEntries[0], "admin") == 0)
                    {
                        if (String.IsNullOrEmpty(apiKey))
                        {
                            _Logging.Log(LoggingModule.Severity.Warn, "RequestReceived admin API requested but no API key specified");
                            resp = new HttpResponse(req, 401, null, "application/json",
                                Encoding.UTF8.GetBytes(new ErrorResponse(401, "No API key specified.", null).ToJson(true)));
                            return resp;
                        }

                        if (String.Compare(_Config.Server.AdminApiKey, apiKey) != 0)
                        {
                            _Logging.Log(LoggingModule.Severity.Warn, "RequestReceived admin API requested but invalid API key specified");
                            resp = new HttpResponse(req, 401, null, "application/json",
                                Encoding.UTF8.GetBytes(new ErrorResponse(401, null, null).ToJson(true)));
                            return resp;
                        }

                        resp = AdminApiHandler(req);
                        return resp;
                    }
                }

                #endregion

                #region Authenticate-Request

                if (!String.IsNullOrEmpty(apiKey))
                {
                    if (!_ApiKey.VerifyApiKey(apiKey, _User, out currUserMaster, out currApiKey, out currApiKeyPermission))
                    {
                        _Logging.Log(LoggingModule.Severity.Warn, "RequestReceived unable to verify API key " + apiKey);
                        resp = new HttpResponse(req, 401, null, "application/json",
                           Encoding.UTF8.GetBytes(new ErrorResponse(401, null, null).ToJson(true)));
                        return resp;
                    }
                }
                else if ((!String.IsNullOrEmpty(email)) && (!String.IsNullOrEmpty(password)))
                {
                    if (!_User.AuthenticateCredentials(email, password, out currUserMaster))
                    {
                        _Logging.Log(LoggingModule.Severity.Warn, "RequestReceived unable to verify credentials for email " + email);
                        resp = new HttpResponse(req, 401, null, "application/json",
                            Encoding.UTF8.GetBytes(new ErrorResponse(401, null, null).ToJson(true)));
                        return resp;
                    }

                    currApiKeyPermission = ApiKeyPermission.DefaultPermit(currUserMaster);
                }
                else
                {
                    _Logging.Log(LoggingModule.Severity.Warn, "RequestReceived user API requested but no authentication material supplied");
                    resp = new HttpResponse(req, 401, null, "application/json",
                        Encoding.UTF8.GetBytes(new ErrorResponse(401, "No authentication material.", null).ToJson(true)));
                    return resp;
                }

                #endregion

                #region Build-and-Validate-Metadata

                md.Http = req;
                md.User = currUserMaster;
                md.ApiKey = currApiKey;
                md.Permission = currApiKeyPermission;

                if (md.Http.QuerystringEntries != null && md.Http.QuerystringEntries.Count > 0)
                {
                    md.Params = RequestParameters.FromDictionary(md.Http.QuerystringEntries);
                }
                else
                {
                    md.Params = new RequestParameters();
                }
                
                if (!String.IsNullOrEmpty(md.Params.Type))
                {
                    List<string> matchVals = new List<string> { "json", "xml", "html", "sql", "text" };
                    if (!matchVals.Contains(md.Params.Type))
                    {
                        _Logging.Log(LoggingModule.Severity.Warn, "RequestReceived invalid 'type' value found in querystring: " + md.Params.Type);
                        resp = new HttpResponse(md.Http, 400, null, "application/json",
                            Encoding.UTF8.GetBytes(new ErrorResponse(400, "Invalid 'type' in querystring, use [json/xml/html/sql/text].", null).ToJson(true)));
                        return resp;
                    }
                }

                #endregion

                #region Call-User-API

                resp = UserApiHandler(md);
                return resp;

                #endregion
            }
            catch (Exception e)
            {
                _Logging.LogException("RequestReceived", "Outer exception", e);
                return resp;
            }
            finally
            {
                _Conn.Close(Thread.CurrentThread.ManagedThreadId);

                _Logging.Log(LoggingModule.Severity.Debug, req.Method + " " + req.RawUrlWithoutQuery + " " + resp.StatusCode + " [" + Common.TotalMsFrom(startTime) + "ms]");

                if (_Config.Logging.LogHttpRequests)
                {
                    _Logging.Log(LoggingModule.Severity.Debug, "RequestReceived sending response: " + Environment.NewLine + resp.ToString());
                } 
            }
        }

        public static HttpResponse OptionsHandler(HttpRequest req)
        {
            _Logging.Log(LoggingModule.Severity.Debug, "OptionsHandler " + Thread.CurrentThread.ManagedThreadId + ": processing options request");

            Dictionary<string, string> responseHeaders = new Dictionary<string, string>();

            string[] requestedHeaders = null;
            if (req.Headers != null)
            {
                foreach (KeyValuePair<string, string> curr in req.Headers)
                {
                    if (String.IsNullOrEmpty(curr.Key)) continue;
                    if (String.IsNullOrEmpty(curr.Value)) continue;
                    if (String.Compare(curr.Key.ToLower(), "access-control-request-headers") == 0)
                    {
                        requestedHeaders = curr.Value.Split(',');
                        break;
                    }
                }
            }

            string headers =
                _Config.Server.HeaderApiKey + ", " +
                _Config.Server.HeaderEmail + ", " +
                _Config.Server.HeaderPassword;

            if (requestedHeaders != null)
            {
                foreach (string curr in requestedHeaders)
                {
                    headers += ", " + curr;
                }
            }

            responseHeaders.Add("Access-Control-Allow-Methods", "OPTIONS, HEAD, GET, PUT, POST, DELETE");
            responseHeaders.Add("Access-Control-Allow-Headers", "*, Content-Type, X-Requested-With, " + headers);
            responseHeaders.Add("Access-Control-Expose-Headers", "Content-Type, X-Requested-With, " + headers);
            responseHeaders.Add("Access-Control-Allow-Origin", "*");
            responseHeaders.Add("Accept", "*/*");
            responseHeaders.Add("Accept-Language", "en-US, en");
            responseHeaders.Add("Accept-Charset", "ISO-8859-1, utf-8");
            responseHeaders.Add("Connection", "keep-alive");

            if (_Config.Server.Ssl)
            {
                responseHeaders.Add("Host", "https://" + _Config.Server.ListenerHostname + ":" + _Config.Server.ListenerPort);
            }
            else
            {
                responseHeaders.Add("Host", "http://" + _Config.Server.ListenerHostname + ":" + _Config.Server.ListenerPort);
            }

            _Logging.Log(LoggingModule.Severity.Debug, "OptionsHandler " + Thread.CurrentThread.ManagedThreadId + ": exiting successfully from OptionsHandler");
            return new HttpResponse(req, 200, responseHeaders, null, null);
        }

        public static bool ExitApplication()
        {
            _Logging.Log(LoggingModule.Severity.Info, "KomodoServer exiting due to console request");
            Environment.Exit(0);
            return true;
        }

        public static string RootHtml()
        {
            string ret =
                "<html>" +
                "  <head>" +
                "    <title>Komodo Server</title>" +
                "  </head>" +
                "  <body>" +
                "    <pre>";

            ret += Welcome();
            ret += "Komodo Server version " + _Version + Environment.NewLine;
            ret += "Information storage, search, and retrieval platform" + Environment.NewLine;
            ret += Environment.NewLine;
            ret += "Documentation and source code: <a href='https://github.com/jchristn/komodo' target='_blank'>https://github.com/jchristn/komodo</a>" + Environment.NewLine;
            ret +=
                "    </pre>" +
                "  </body>" +
                "</html>";
            return ret;
        }
    }
}
