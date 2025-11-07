using System;

namespace Mumrich.DDD.Stereotypes.Aggregate;

public record AggregateUpdateEvent<TAggregate, TModel>(Guid AggregateId, TModel Model)
  : AggregateEventBase<TAggregate>(AggregateId);
