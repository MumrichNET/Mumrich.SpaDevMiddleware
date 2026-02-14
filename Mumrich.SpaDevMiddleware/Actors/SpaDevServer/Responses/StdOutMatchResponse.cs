using System;

namespace Ingtes.Success.Framework.Host.SpaMiddleware.Actors.SpaDevServer.Responses;

public record StdOutMatchResponse(bool Success, Exception? Exception = null);
