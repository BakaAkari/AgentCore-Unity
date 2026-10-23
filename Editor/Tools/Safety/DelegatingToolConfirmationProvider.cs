using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// Delegates tool confirmation requests to a caller-provided asynchronous handler.
    /// </summary>
    public sealed class DelegatingToolConfirmationProvider : IToolConfirmationProvider, IToolSessionTrustState
    {
        private readonly Func<ToolConfirmationRequest, CancellationToken, Task<bool>> _handler;
        private readonly Func<bool> _isYoloActive;

        /// <summary>
        /// Creates a delegating confirmation provider.
        /// </summary>
        /// <param name="handler">The asynchronous confirmation handler.</param>
        /// <param name="isYoloActive">
        /// Optional query for the session YOLO state (owned by the UI). When it returns true the
        /// dispatcher skips user-configured deny rules. Null means "never YOLO".
        /// </param>
        public DelegatingToolConfirmationProvider(
            Func<ToolConfirmationRequest, CancellationToken, Task<bool>> handler,
            Func<bool> isYoloActive = null)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _isYoloActive = isYoloActive;
        }

        /// <inheritdoc />
        public bool IsYoloActive
        {
            get
            {
                try
                {
                    return _isYoloActive != null && _isYoloActive();
                }
                catch
                {
                    return false;
                }
            }
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
