using FluentFTP;
using FreeFlow.Core.Models;

namespace FreeFlow.Core.Services;

internal static class FtpClientOptions
{
    public static void Configure(AsyncFtpClient client, FtpProtocol protocol)
    {
        if (protocol == FtpProtocol.Ftps)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.DataConnectionEncryption = true;
        }

        client.Config.ConnectTimeout = 5000;
        client.Config.ReadTimeout = 10000;
        client.Config.DataConnectionConnectTimeout = 5000;
        client.Config.DataConnectionReadTimeout = 10000;
    }
}
