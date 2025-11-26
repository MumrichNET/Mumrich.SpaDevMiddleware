using Akka.Persistence.Fsm;

namespace Mumrich.DDD.Stereotypes.Aggregate;

public interface IAggregateState : PersistentFSM.IFsmState { }
