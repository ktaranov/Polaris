﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Management.Automation;
using System.Threading;
using System.Management.Automation.Runspaces;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.IO;
using System.Security.Principal;

namespace PolarisCore
{
    public class Polaris
    {
        private Action<string> Logger {get; set;}
        public int Port {get; set;}

        // path => method => script block
        public Dictionary<string, Dictionary<string, string>> ScriptBlockRoutes
            = new Dictionary<string, Dictionary<string, string>>();

        public List<PolarisMiddleware> RouteMiddleware
            = new List<PolarisMiddleware>();

        HttpListener Listener;

        RunspacePool PowerShellPool;

        bool StopServer = false;

        public Polaris(Action<string> logger)
        {
            Logger = logger;
        }

        public void AddRoute(string path, string method, string scriptBlock)
        {
            if (scriptBlock == null)
            {
                throw new ArgumentNullException(nameof(scriptBlock));
            }

            string sanitizedPath = path.TrimEnd('/').TrimStart('/');

            if(!ScriptBlockRoutes.ContainsKey(sanitizedPath))
            {
                ScriptBlockRoutes[sanitizedPath] = new Dictionary<string, string>();
            }
            ScriptBlockRoutes[sanitizedPath][method] = scriptBlock;
        }

        public void RemoveRoute(string path, string method)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            if (method == null)
            {
                throw new ArgumentNullException(nameof(method));
            }

            string sanitizedPath = path.TrimEnd('/').TrimStart('/');
            ScriptBlockRoutes[sanitizedPath].Remove(method);
            if (ScriptBlockRoutes[sanitizedPath].Count == 0)
            {
                ScriptBlockRoutes.Remove(sanitizedPath);
            }
        }

        public void AddMiddleware(string name, string scriptBlock)
        {
            if (scriptBlock == null)
            {
                throw new ArgumentNullException(nameof(scriptBlock));
            }
            RouteMiddleware.Add(new PolarisMiddleware {
                Name = name,
                ScriptBlock = scriptBlock
            });
        }

        public void RemoveMiddleware(string name)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }
            RouteMiddleware.RemoveAll(m => m.Name == name);
        }

        public void Start(int port = 3000, int minRunspaces = 1, int maxRunspaces = 1)
        {
            StopServer = false;

            PowerShellPool = RunspaceFactory.CreateRunspacePool(minRunspaces, maxRunspaces);
            PowerShellPool.Open();
            Listener = InitListener(port);

            Thread listenerThread = new Thread(async () => { await ListenerLoop(); });
            listenerThread.Start();

            // Loop until worker thread activates.
            while (!listenerThread.IsAlive);
            Log("App listening on Port: " + port + "!");
        }

        public void Stop()
        {
            StopServer = true;
            //Listener.Close();
            PowerShellPool.Dispose();
            Log("Server Stopped.");
        }

        public HttpListener InitListener(int port)
        {
            Port = port;

            HttpListener listener = new HttpListener();

            // If user is on a non-windows system or windows as administrator
            if (Environment.OSVersion.Platform != PlatformID.Win32NT ||
                (Environment.OSVersion.Platform == PlatformID.Win32NT &&
                (new WindowsPrincipal(WindowsIdentity.GetCurrent())).IsInRole(WindowsBuiltInRole.Administrator)))
            {
                listener.Prefixes.Add("http://+:" + Port + "/");
            } else
            {
                listener.Prefixes.Add("http://localhost:" + Port + "/");
            }

            listener.Start();
            return listener;
        }

        public async Task ListenerLoop()
        {
            while(!StopServer)
            {
                HttpListenerContext context = null;
                try
                {
                    context = await Listener.GetContextAsync();
                }
                catch (Exception e)
                {
                    if (!(e is ObjectDisposedException))
                    {
                        throw;
                    }
                }

                if(StopServer || context == null)
                {
                    if (Listener != null)
                    {
                        Listener.Close();
                    }
                    break;
                }

                HttpListenerRequest rawRequest = context.Request;
                HttpListenerResponse rawResponse = context.Response;

                Log("request came in: " + rawRequest.HttpMethod + " " + rawRequest.RawUrl);

                PolarisRequest request = new PolarisRequest(rawRequest);
                PolarisResponse response = new PolarisResponse();

                string route = rawRequest.Url.AbsolutePath.TrimEnd('/').TrimStart('/');
                PowerShell PowerShellInstance = PowerShell.Create();
                PowerShellInstance.RunspacePool = PowerShellPool;
                try
                {
                    // Set up PowerShell instance by making request and response global
                    PowerShellInstance.AddScript(PolarisHelperScripts.InitializeRequestAndResponseScript);
                    PowerShellInstance.AddParameter("req", request);
                    PowerShellInstance.AddParameter("res", response);

                    // Run middleware in the order in which it was added
                    foreach (PolarisMiddleware middleware in RouteMiddleware)
                    {
                        PowerShellInstance.AddScript(middleware.ScriptBlock);
                    }

                    PowerShellInstance.AddScript(ScriptBlockRoutes[route][rawRequest.HttpMethod]);

                    var res = PowerShellInstance.BeginInvoke<PSObject>(new PSDataCollection<PSObject>(), new PSInvocationSettings(), (result) => {
                        if (PowerShellInstance.InvocationStateInfo.State == PSInvocationState.Failed)
                        {
                            Log(PowerShellInstance.InvocationStateInfo.Reason.ToString());
                            response.Send(PowerShellInstance.InvocationStateInfo.Reason.ToString());
                            response.SetStatusCode(500);
                        }
                        Send(rawResponse, response);
                        PowerShellInstance.Dispose();
                    }, null);
                }
                catch(Exception e)
                {
                    if(e is KeyNotFoundException)
                    {
                        Send(rawResponse, System.Text.Encoding.UTF8.GetBytes("Not Found"), 404, "text/plain; charset=UTF-8");
                        Log("404 Not Found");
                    }
                    else
                    {
                        Log(e.Message);
                        throw e;
                    }
                }
            }
        }

        private static void Send(HttpListenerResponse rawResponse, PolarisResponse response)
        {
            Send(rawResponse, response.ByteResponse, response.StatusCode, response.ContentType);
        }

        private static void Send(HttpListenerResponse rawResponse, byte[] byteResponse, int statusCode, string contentType)
        {
            rawResponse.StatusCode = statusCode;
            rawResponse.ContentType = contentType;
            rawResponse.ContentLength64 = byteResponse.Length;
            rawResponse.OutputStream.Write(byteResponse, 0, byteResponse.Length);
            rawResponse.OutputStream.Close();
        }

        private void Log(string logString)
        {
            try
            {
                Logger(logString);
            } catch(Exception e)
            {
                if (!(e is PSInvalidOperationException))
                {
                    Console.WriteLine(e.Message);
                    Console.WriteLine(logString);
                }
            }
        }
    }
}
