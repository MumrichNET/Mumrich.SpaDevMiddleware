using System;

namespace Mumrich.DDD.Stereotypes.Aggregate;

public record AggregateReaderResponse<TAggregate, TModel>(Guid AggregateId, TModel Model);
