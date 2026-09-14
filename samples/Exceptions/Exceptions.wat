(module
  (type $raiser (func (param i32)))
  (tag $value (param i32))
  (tag $other (param i32))
  (export "value" (tag $value))
  (func $raise (type $raiser)
    local.get 0
    throw $value)
  (func (export "direct") (param i32) (result i32)
    try (result i32)
      local.get 0
      call $raise
      i32.const 0
    catch $value
      i32.const 1
      i32.add
    end)
  (func (export "try_branch") (param i32) (result i32)
    try (result i32)
      local.get 0
      br 0
      i32.const 0
    catch_all
      i32.const 7
    end)
  (func (export "catch_branch") (result i32)
    try (result i32)
      i32.const 0
      throw $value
      i32.const 0
    catch $value
      drop
      i32.const 41
      br 0
      i32.const 0
    end)
  (func (export "indirect") (param i32) (result i32)
    try (result i32)
      local.get 0
      i32.const 0
      call_indirect (type $raiser)
      i32.const 0
    catch $value
      i32.const 1
      i32.add
    end)
  (func (export "f") (param i32) (result i32)
    try (result i32)
      local.get 0
      throw $value
      i32.const 0
    catch $value
      i32.const 1
      i32.add
    end)
  (func (export "catch_all") (result i32)
    try (result i32)
      i32.const 9
      throw $value
      i32.const 0
    catch_all
      i32.const 7
    end)
  (func (export "rethrow") (param i32) (result i32)
    try (result i32)
      try (result i32)
        local.get 0
        throw $value
        i32.const 0
      catch $value
        rethrow 0
      end
    catch $value
      i32.const 1
      i32.add
    end)
  (func (export "different") (result i32)
    try (result i32)
      i32.const 9
      throw $value
      i32.const 0
    catch $other
      drop
      i32.const 1
    catch_all
      i32.const 2
    end)
  (func (export "trap") (result i32)
    try (result i32)
      unreachable
      i32.const 0
    catch_all
      i32.const 7
    end)
  (func (export "branch") (param i32) (result i32)
    block (result i32)
      try (result i32)
        local.get 0
        br 1
        i32.const 0
      catch_all
        i32.const 7
      end
    end)
  (table 1 funcref)
  (elem (i32.const 0) $raise))
