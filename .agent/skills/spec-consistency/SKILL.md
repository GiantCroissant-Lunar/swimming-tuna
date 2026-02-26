---
name: spec-consistency
description: Cross-validate OpenAPI specs, JSON schemas, and DTO signatures for consistency
tags: [openapi, schema, dto, nullability, spec, api, validation]
roles: [reviewer, builder]
scope: project
---

# Spec Consistency

Cross-validate OpenAPI specifications, JSON schemas, and DTO signatures to ensure consistency across the API surface.

## What to Check

### Field Nullability
- Verify nullable fields in OpenAPI specs match nullable properties in DTOs
- Check that required arrays include only non-nullable properties
- Ensure optional fields are properly marked in all three locations

### Enum Values
- Enum values in spec must match code constants exactly
- Check for case sensitivity and string format consistency
- Verify enum descriptions are synchronized

### Model Generation
- Check if generated models are out of sync with specs
- Flag when specs change but generated code hasn't been regenerated
- Validate that DTO signatures match the generated models

## Common Issues

- Spec marks field as required but DTO has nullable property
- Enum added to code but not reflected in OpenAPI spec
- Generated models stale after spec update
- Array items marked nullable in spec but non-nullable in schema
