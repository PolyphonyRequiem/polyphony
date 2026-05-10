"""Path-coverage harness driver for polyphony workflows.

See ``tests/harness/README.md`` for the layout, fixture format, and
extension model. The driver wires conductor's ``WorkflowEngine`` to a
``FakeProvider`` that returns scripted ``AgentOutput`` per agent, runs
the workflow, captures the event trace, and asserts the trace satisfies
the scenario's expectations.
"""
