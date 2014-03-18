namespace SocketIOClient.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.Diagnostics;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    public static class DownloadTasks
    {
        public static Task<string> DownloadString(string uri, NameValueCollection headers = null)
        {
            return DownloadString(new Uri(uri), headers);
        }

        public static Task<string> DownloadString(Uri uri, NameValueCollection headers = null)
        {
            var taskSource = new TaskCompletionSource<string>();

            var webClient = new WebClient();
            if (headers != null && headers.Count > 0) webClient.Headers.Add(headers);
            webClient.DownloadStringCompleted += (sender, args) =>
            {
                try
                {
                    if (args.Error != null)
                    {
                        Trace.TraceError("Web request error");
                        taskSource.SetException(args.Error);
                    }
                    else if (args.Cancelled)
                    {
                        Trace.TraceError("Web request canceled");
                        taskSource.SetCanceled();
                    }
                    else
                    {
                        Trace.TraceInformation("Web request response" + Thread.CurrentThread.ManagedThreadId.ToString());
                        taskSource.SetResult(args.Result);
                    }
                }
                finally
                {
                    webClient.Dispose();
                }
            };
            Trace.TraceInformation("Web request" + Thread.CurrentThread.ManagedThreadId.ToString());
            ThreadPool.QueueUserWorkItem(
                state =>
                    {
                        webClient.DownloadStringAsync(uri);
                    });
            

            return taskSource.Task;
        }
    }
}
