using System;

namespace Mumrich.SpaDevMiddleware.Actors.SpaDevServer.Responses;

public record StdOutMatchResponse(bool Success, Exception? Exception = null);
