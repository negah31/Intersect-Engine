﻿using Intersect.Server.Core.CommandParsing;
using Intersect.Server.Database.PlayerData;
using Intersect.Server.Database.PlayerData.Security;
using Intersect.Server.Localization;

namespace Intersect.Server.Core.Commands
{

    internal partial class ApiRolesCommand : TargetUserCommand
    {

        public ApiRolesCommand() : base(Strings.Commands.ApiRoles, Strings.Commands.Arguments.TargetApi)
        {
        }

        protected override void HandleTarget(ServerContext context, ParserResult result, User target)
        {
            if (target == null)
            {
                Console.WriteLine($@"    {Strings.Account.NotFound}");

                return;
            }

            if (target.Power == null)
            {
                throw new ArgumentNullException(nameof(target.Power));
            }

            Console.WriteLine(Strings.Commandoutput.ApiRoles.ToString(target.Name));
            if (target.Power.ApiRoles is {} apiRoles)
            {
                Console.WriteLine($"{nameof(ApiRoles.UserQuery)}: {apiRoles.UserQuery}");
                Console.WriteLine($"{nameof(ApiRoles.UserManage)}: {apiRoles.UserManage}");
            }
            else
            {
                Console.WriteLine(Strings.Commandoutput.ApiRolesNotGranted);
            }
        }

    }

}
