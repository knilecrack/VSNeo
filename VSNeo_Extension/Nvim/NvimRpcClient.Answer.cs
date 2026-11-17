// Answering nvim's own requests (rpcrequest from the companion).

using System;
using System.Threading;
using System.Threading.Tasks;

namespace VSNeo_Extension.Nvim;
internal sealed partial class NvimRpcClient
{
    /// <summary>
    /// Answers nvim's requests (vim.rpcrequest). Raised off the read thread;
    /// the returned value goes back as the result, an exception as the error.
    /// Null means no handler: nvim gets an error rather than a hang.
    /// </summary>
    public Func<string, object[], Task<object?>>? RequestHandler { get; set; }

    /// <summary>
    /// nvim blocks its whole main loop on an rpcrequest until the answer
    /// arrives, so an answer is always sent - a result, an error, or a
    /// timeout error after this long. A request that never answered would
    /// freeze every key.
    /// </summary>
    private static readonly TimeSpan RequestAnswerTimeout = TimeSpan.FromSeconds(3);

    private async Task AnswerRequestAsync(long msgId, string method, object[] args)
    {
        object? result = null;
        object? error = null;
        try
        {
            var handler = RequestHandler;
            if (handler == null)
            {
                error = "VSNeo has no handler for " + method;
            }
            else
            {
                var work = Task.Run(() => handler(method, args));
                var done = await Task.WhenAny(work, Task.Delay(RequestAnswerTimeout)).ConfigureAwait(false);
                if (done == work)
                {
                    result = await work.ConfigureAwait(false);
                }
                else
                {
                    error = "VSNeo did not answer " + method + " in time";
                }
            }
        }
        catch (Exception ex)
        {
            error = method + " failed: " + ex.GetBaseException().Message;
            Infrastructure.Log.Write("nvim request " + method + " failed", ex.GetBaseException());
        }

        try
        {
            await SendAsync(new object[] { 1L, msgId, error!, result! }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                Infrastructure.Log.Write("could not answer nvim's " + method + " request", ex);
            }
        }
    }
}
