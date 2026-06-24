using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// Delegates tool confirmation requests to a caller-provided asynchronous handler.
    /// </summary>
    public sealed class DelegatingToolConfirmationProvider : IToolConfirmationProvider
    {
        private readonly Func<ToolConfirmationRequest, CancellationToken, Task<bool>> _handler;

        /// <summary>
        /// Creates a delegating confirmation provider.
        /// </summary>
        /// <param name="handler">The asynchronous confirmation handler.</param>
        public DelegatingToolConfirmationProvider(Func<ToolConfirmationRequest, CancellationToken, Task<bool>> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <inheritdoc />
        public Task<bool> RequestConfirmationAsync(ToolConfirmationRequest request, CancellationToken ct)
        {
            if (request == null || ct.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            try
            {
                return _handler(request, ct) ?? Task.FromResult(false);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }
    }
}
