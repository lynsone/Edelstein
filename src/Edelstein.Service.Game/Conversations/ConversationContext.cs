using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Edelstein.Core.Services;
using Edelstein.Network;
using Edelstein.Network.Packet;
using Foundatio.AsyncEx;

namespace Edelstein.Service.Game.Conversations
{
    public class ConversationContext : IConversationContext
    {
        public ISocket Socket { get; }
        public CancellationTokenSource TokenSource { get; }
        public IMessage PreviousMessage { get; private set; }
        public AsyncProducerConsumerQueue<object> Responses { get; }

        public ConversationContext(ISocket socket, CancellationTokenSource tokenSource)
        {
            Socket = socket;
            TokenSource = tokenSource;
            Responses = new AsyncProducerConsumerQueue<object>();
        }

        public ConversationContext(ISocket socket)
            : this(socket, new CancellationTokenSource())
        {
        }

        public async Task<T> Send<T>(IMessage message)
        {
            PreviousMessage = message;

            using (var p = new Packet(SendPacketOperations.ScriptMessage))
            {
                message.Encode(p);
                await Socket.SendPacket(p);
            }

            var response = (T) await Responses.DequeueAsync(TokenSource.Token);

            if (!PreviousMessage.Validate(response)) throw new InvalidDataException("Invalid response value");
            return response;
        }

        public void Dispose()
        {
            TokenSource?.Cancel();
            TokenSource?.Dispose();
            Responses?.CompleteAdding();
        }
    }
}