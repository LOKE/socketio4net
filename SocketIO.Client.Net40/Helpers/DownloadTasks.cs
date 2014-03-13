namespace SocketIOClient.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.Net;
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
                        taskSource.SetException(args.Error);
                    }
                    else if (args.Cancelled)
                    {
                        taskSource.SetCanceled();
                    }
                    else
                    {
                        taskSource.SetResult(args.Result);
                    }
                }
                finally
                {
                    webClient.Dispose();
                }
            };
            webClient.DownloadStringAsync(uri);

            return taskSource.Task;
        }
    }
}
