﻿using System.Threading.Tasks;
using SapphireDb.Connection;
using SapphireDb.Internal;
using SapphireDb.Models;

namespace SapphireDb.Command.Unsubscribe
{
    class UnsubscribeCommandHandler : CommandHandlerBase, ICommandHandler<UnsubscribeCommand>, INeedsConnection
    {
        public ConnectionBase Connection { get; set; }

        public UnsubscribeCommandHandler(DbContextAccesor dbContextAccessor)
            : base(dbContextAccessor)
        {

        }

        public async Task<ResponseBase> Handle(HttpInformation context, UnsubscribeCommand command)
        {
            await Connection.RemoveSubscription(command);
            return null;
        }
    }
}
