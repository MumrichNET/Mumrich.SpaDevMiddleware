using System;

namespace Mumrich.DDD.Stereotypes.Aggregate;

public abstract record AggregateEventBase<TAggregate>(Guid AggregateId);
