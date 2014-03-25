namespace SocketIOClient.Helpers
{
    using System;
    using System.Threading.Tasks;

    public static class TaskExtensions
    {
        /// <summary>
        /// Throws the first inner exception if faulted.
        /// </summary>
        public static void ThrowIfFailed(this Task task)
        {
            if (task.Exception != null) task.Exception.HandleAndRethrowFirst();
            if (task.IsFaulted) throw new ApplicationException("Task faulted");
            if (task.IsCanceled) throw new OperationCanceledException();
        }

        /// <summary>
        /// Handles the exception if exists.
        /// </summary>
        public static void HandleIfFailed(this Task task)
        {
            if (task.Exception != null) task.Exception.HandleAll();
        }
    }
}
