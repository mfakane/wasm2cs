(module
  (type $increment (func (param i32) (result i32)))
  (type $call (func (param i32 i32) (result i32)))
  (func $increment (type $increment)
    local.get 0
    i32.const 1
    i32.add)
  (func $call (type $call)
    local.get 0
    local.get 1
    call_indirect (type $increment))
  (table (export "table") 1 1 funcref)
  (elem (i32.const 0) $increment)
  (export "call" (func $call)))
