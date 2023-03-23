using System;

namespace Mumrich.DDD.Stereotypes.Aggregate;

public record AggregateReaderQuery<TAggregate>(Guid AggregateId);
