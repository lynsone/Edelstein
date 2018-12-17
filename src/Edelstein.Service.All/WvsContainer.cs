using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Edelstein.Core.Services;
using Edelstein.Service.Game;
using Edelstein.Service.Login;
using Foundatio.Caching;
using Foundatio.Messaging;
using MoreLinq;

namespace Edelstein.Service.All
{
    public class WvsContainer : IService
    {
        private readonly WvsContainerOptions _options;
        private readonly ICacheClient _cache;
        private readonly IMessageBus _messageBus;
        private readonly ICollection<IService> _services;

        public WvsContainer(
            WvsContainerOptions options,
            ICacheClient cache,
            IMessageBus messageBus
        )
        {
            _options = options;
            _cache = cache;
            _messageBus = messageBus;
            _services = new List<IService>();
        }

        public Task Start()
        {
            _options.LoginServices
                .Select(o => new WvsLogin(o, _cache, _messageBus))
                .ForEach(_services.Add);
            _options.GameServices
                .Select(o => new WvsGame(o, _cache, _messageBus))
                .ForEach(_services.Add);
            return Task.WhenAll(_services.Select(s => s.Start()));
        }

        public Task Stop()
            => Task.WhenAll(_services.Select(s => s.Stop()));
    }
}