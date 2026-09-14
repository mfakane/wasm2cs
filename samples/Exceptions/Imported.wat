(module
  (import "env" "value" (tag $value (param i32)))
  (func (export "f") (param i32) (result i32)
    try (result i32)
      local.get 0
      throw $value
      i32.const 0
    catch $value
      i32.const 1
      i32.add
    end))
