---
name: tdd-pattern
description: Follow test-driven development with Arrange-Act-Assert pattern and meaningful assertions
tags: [tdd, test, testing, unit, integration, assertion]
roles: [builder, reviewer]
scope: project
---

# TDD Pattern

Follow test-driven development practices with proper test structure and meaningful assertions.

## Write Test First

1. Write a failing test that describes the desired behavior
2. Implement the minimal code to make the test pass
3. Refactor while keeping tests green

This ensures code is testable and requirements are clear before implementation.

## Arrange-Act-Assert Pattern

Structure tests clearly:

```csharp
[Fact]
public void ProcessOrder_WithValidInput_ReturnsOrderId()
{
    // Arrange
    var service = new OrderService();
    var order = new Order { CustomerId = 123, Amount = 99.99m };

    // Act
    var result = service.ProcessOrder(order);

    // Assert
    Assert.NotNull(result);
    Assert.True(result.OrderId > 0);
}
```

## Test Behavior Not Implementation

Test meaningful behavior, not just serialization or data transfer:

```csharp
// ❌ BAD - just testing serialization
Assert.Equal("John", user.Name);

// ✅ GOOD - testing business logic
Assert.True(order.CanBeShipped());
Assert.Equal(OrderStatus.Pending, order.Status);
```

## Mock External Dependencies

Isolate units under test:

- Mock HTTP clients, database contexts, file systems
- Use dependency injection to enable mocking
- Verify mock interactions when testing side effects

## Edge Cases and Error Paths

Test beyond the happy path:

- Null or empty inputs
- Boundary values (max, min, zero)
- Invalid states or preconditions
- Exception scenarios
- Concurrent access patterns (when relevant)

## Integration Tests

For integration tests:
- Test actual interactions between components
- Use test databases or containers
- Clean up test data after each test
- Keep them fast enough to run frequently
