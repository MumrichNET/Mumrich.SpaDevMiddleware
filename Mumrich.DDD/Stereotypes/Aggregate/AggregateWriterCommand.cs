using System;

namespace Mumrich.DDD.Stereotypes.Aggregate;

public record AggregateWriterCommand<TAggregate>(Guid AggregateId) : AggregateEventBase<TAggregate>(AggregateId);
