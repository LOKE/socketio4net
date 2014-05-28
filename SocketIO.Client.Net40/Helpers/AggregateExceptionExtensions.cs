namespace SocketIOClient.Helpers
{
    using System;

    public static class AggregateExceptionExtensions
    {
        /// <summary>
        /// Handles all exceptions within the and returns first.
        /// </summary>
        /// <param name="ae">The ae.</param>
        public static Exception HandleAndGetFirst(this AggregateException ae)
        {
            // flatten any nested aggreg exs
            var flattened = ae.Flatten();

            // grab the first inner exception
            var innerEx = flattened.InnerExceptions[0];

            // mark all exceptions as handled
            flattened.Handle(x => true);

            // return first exception
            return innerEx;
        }

        /// <summary>
        /// Handles all exceptions within the and rethrow first.
        /// </summary>
        /// <param name="ae">The ae.</param>
        public static void HandleAndRethrowFirst(this AggregateException ae)
        {
            // flatten any nested aggreg exs
            var flattened = ae.Flatten();

            // grab the first inner exception
            var innerEx = flattened.InnerExceptions[0];

            // mark all exceptions as handled
            flattened.Handle(x => true);

            // rethrow first exception
            throw innerEx;
        }

        /// <summary>
        /// Handles all exceptions within.
        /// </summary>
        /// <param name="ae">The ae.</param>
        public static void HandleAll(this AggregateException ae)
        {
            ae.Flatten().Handle(x => true);
        }
    }
}
