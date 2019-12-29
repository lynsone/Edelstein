namespace Edelstein.Core.Services.Distributed.States
{
    public class LoginServiceState : IServerNodeState
    {
        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }

        public short Version { get; set; }
        public string Patch { get; set; }
        public byte Locale { get; set; }
    }
}