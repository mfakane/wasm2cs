;; Repository-owned SH-02 fixture. Numeric values other than i32 only cross typed
;; signatures/locals/select; their numeric instructions remain future work.
(module
  (func $pair (param i32 i32) (result i32 i32) local.get 0 local.get 1)
  (func (export "call_pair") (param i32 i32) (result i32 i32)
    local.get 0 local.get 1 call $pair)
  (func (export "block_args") (param i32 i32) (result i32 i32)
    local.get 0 local.get 1
    block (param i32 i32) (result i32 i32)
      i32.const 1 i32.add
    end)
  (func (export "prefix") (param i32) (result i32 i32)
    i32.const 99 local.get 0 block (param i32) (result i32) i32.const 1 i32.add end)
  (func (export "implicit_else") (param i32 i32) (result i32)
    local.get 0 local.get 1 if (param i32) (result i32) i32.const 1 i32.add end)
  (func (export "explicit_else") (param i32 i32 i32) (result i32 i32)
    local.get 0 local.get 1 local.get 2
    if (param i32 i32) (result i32 i32) i32.const 1 i32.add
    else i32.const 2 i32.add end)
  (func (export "branch_if") (param i32 i32 i32) (result i32 i32)
    block (result i32 i32)
      local.get 0 local.get 1 local.get 2 br_if 0
      i32.const 1 i32.add
    end)
  (func (export "branch") (param i32 i32) (result i32 i32)
    block (result i32 i32)
      local.get 0 local.get 1 br 0
      unreachable
    end)
  (func (export "branch_function") (param i32 i32) (result i32 i32)
    local.get 0 local.get 1 br 0)
  (func (export "early") (param i32 i32) (result i32 i32)
    local.get 0 local.get 1 return unreachable)
  (func (export "table") (param i32) (result i32 i32)
    block (result i32 i32)
      block (result i32 i32)
        i32.const 10 i32.const 20 local.get 0 br_table 0 1
      end
      i32.const 1 i32.add
    end)
  (func (export "sum") (param i32) (result i32) (local i32 i32)
    i32.const 0 local.get 0
    loop (param i32 i32) (result i32)
      local.set 2 local.set 1
      local.get 2 i32.eqz
      if (result i32) local.get 1
      else
        local.get 1 local.get 2 i32.add
        local.get 2 i32.const 1 i32.sub br 1
      end
    end)
  (func (export "swap") (param i32 i32 i32) (result i32 i32) (local i32)
    local.get 0 local.get 1 local.get 2
    loop (param i32 i32 i32) (result i32 i32)
      local.set 3 local.get 3 i32.eqz
      if (param i32 i32) (result i32 i32)
      else
        local.set 1 local.set 0 local.get 1 local.get 0
        local.get 3 i32.const 1 i32.sub br 1
      end
    end)
  (func (export "mixed") (param i64 f32 f64) (result i64 f32 f64)
    (local i64 f32 f64)
    local.get 0 local.set 3 local.get 1 local.tee 4 drop local.get 2 local.set 5
    local.get 3 local.get 4 local.get 5
    block (param i64 f32 f64) (result i64 f32 f64) end)
  (func (export "select64") (param i64 i64 i32) (result i64)
    local.get 0 local.get 1 local.get 2 select)
  (func (export "select32f") (param f32 f32 i32) (result f32)
    local.get 0 local.get 1 local.get 2 select)
  (func (export "defaults") (result i64 f32 f64) (local i64 f32 f64)
    local.get 0 local.get 1 local.get 2)
  (func $dead (export "dead") (result i64 f32 f64)
    unreachable select drop
    block (param f32) (result f32) end drop)
  ;; WABT 1.0.41 emits malformed command JSON for multi-result assert_trap.
  (func (export "dead_wrapper") call $dead drop drop drop)
  (func (export "polymorphic_table")
    block (result i32) block (result i64) unreachable br_table 0 1 end drop unreachable end drop)
  (func (export "nothing"))
  (func (export "eight") (result i32 i32 i32 i32 i32 i32 i32 i32)
    i32.const 1 i32.const 2 i32.const 3 i32.const 4 i32.const 5 i32.const 6 i32.const 7 i32.const 8)
  (global (export "g") i32 (i32.const 42)))
(assert_return (invoke "call_pair" (i32.const -2147483648) (i32.const 2147483647)) (i32.const -2147483648) (i32.const 2147483647))
(assert_return (invoke "block_args" (i32.const 7) (i32.const 2147483647)) (i32.const 7) (i32.const -2147483648))
(assert_return (invoke "prefix" (i32.const 4)) (i32.const 99) (i32.const 5))
(assert_return (invoke "implicit_else" (i32.const 9) (i32.const 0)) (i32.const 9))
(assert_return (invoke "implicit_else" (i32.const 9) (i32.const -1)) (i32.const 10))
(assert_return (invoke "explicit_else" (i32.const 7) (i32.const 9) (i32.const 0)) (i32.const 7) (i32.const 11))
(assert_return (invoke "explicit_else" (i32.const 7) (i32.const 9) (i32.const 1)) (i32.const 7) (i32.const 10))
(assert_return (invoke "branch_if" (i32.const 7) (i32.const 9) (i32.const 0)) (i32.const 7) (i32.const 10))
(assert_return (invoke "branch_if" (i32.const 7) (i32.const 9) (i32.const -1)) (i32.const 7) (i32.const 9))
(assert_return (invoke "branch" (i32.const 7) (i32.const 9)) (i32.const 7) (i32.const 9))
(assert_return (invoke "branch_function" (i32.const 7) (i32.const 9)) (i32.const 7) (i32.const 9))
(assert_return (invoke "early" (i32.const 7) (i32.const 9)) (i32.const 7) (i32.const 9))
(assert_return (invoke "table" (i32.const 0)) (i32.const 10) (i32.const 21))
(assert_return (invoke "table" (i32.const 1)) (i32.const 10) (i32.const 20))
(assert_return (invoke "table" (i32.const -1)) (i32.const 10) (i32.const 20))
(assert_return (invoke "sum" (i32.const 0)) (i32.const 0))
(assert_return (invoke "sum" (i32.const 10)) (i32.const 55))
(assert_return (invoke "swap" (i32.const 17) (i32.const 29) (i32.const 0)) (i32.const 17) (i32.const 29))
(assert_return (invoke "swap" (i32.const 17) (i32.const 29) (i32.const 1)) (i32.const 29) (i32.const 17))
(assert_return (invoke "swap" (i32.const 17) (i32.const 29) (i32.const 4)) (i32.const 17) (i32.const 29))
(assert_return (invoke "mixed" (i64.const 0x8000000000000001) (f32.const -0) (f64.const -0)) (i64.const 0x8000000000000001) (f32.const -0) (f64.const -0))
(assert_return (invoke "mixed" (i64.const 0x7fffffffffffffff) (f32.const inf) (f64.const -inf)) (i64.const 0x7fffffffffffffff) (f32.const inf) (f64.const -inf))
(assert_return (invoke "mixed" (i64.const -1) (f32.const 0x1p-149) (f64.const 0x1p-1074)) (i64.const -1) (f32.const 0x1p-149) (f64.const 0x1p-1074))
(assert_return (invoke "mixed" (i64.const 9007199254740993) (f32.const nan:0x400123) (f64.const -nan:0x8000000001234)) (i64.const 9007199254740993) (f32.const nan:0x400123) (f64.const -nan:0x8000000001234))
(assert_return (invoke "mixed" (i64.const 0) (f32.const nan) (f64.const nan)) (i64.const 0) (f32.const nan:canonical) (f64.const nan:arithmetic))
(assert_return (invoke "select64" (i64.const 9007199254740993) (i64.const -1) (i32.const 1)) (i64.const 9007199254740993))
(assert_return (invoke "select64" (i64.const 9007199254740993) (i64.const -1) (i32.const 0)) (i64.const -1))
(assert_return (invoke "select32f" (f32.const -0) (f32.const 0) (i32.const 1)) (f32.const -0))
(assert_return (invoke "defaults") (i64.const 0) (f32.const 0) (f64.const 0))
(assert_return (invoke "nothing"))
(assert_return (invoke "eight") (i32.const 1) (i32.const 2) (i32.const 3) (i32.const 4) (i32.const 5) (i32.const 6) (i32.const 7) (i32.const 8))
(assert_return (get "g") (i32.const 42))
(assert_trap (invoke "dead_wrapper") "unreachable")
(assert_trap (invoke "polymorphic_table") "unreachable")
;; Same height, wrong types, including concrete values after unreachable.
(assert_invalid (module (func (param i64) (result i32) local.get 0)) "type mismatch")
(assert_invalid (module (func (param i64) (result i32) unreachable local.get 0)) "type mismatch")
(assert_invalid (module (func (param i64) local.get 0 i32.eqz drop)) "type mismatch")
(assert_invalid (module (func (param i64) unreachable local.get 0 i32.eqz drop)) "type mismatch")
(assert_invalid (module (func (param i32 i64) local.get 1 local.set 0)) "type mismatch")
(assert_invalid (module (func (param i32 i64) local.get 1 local.tee 0 drop)) "type mismatch")
(assert_invalid (module (func $f (param i64)) (func i32.const 1 call $f)) "type mismatch")
(assert_invalid (module (global (mut i32) (i32.const 0)) (func (param i64) local.get 0 global.set 0)) "type mismatch")
(assert_invalid (module (global i64 (i32.const 0))) "type mismatch")
(assert_invalid (module (func (param i32 i64) (result i32) local.get 0 local.get 1 i32.const 0 select)) "type mismatch")
(assert_invalid (module (func (param i64) local.get 0 if end)) "type mismatch")
(assert_invalid (module (func (param i32) local.get 0 block (param i64) drop end)) "type mismatch")
(assert_invalid (module (func (param i64) block (result i32) local.get 0 br 0 end drop)) "type mismatch")
(assert_invalid (module (func (param i64) block (result i32) local.get 0 i32.const 0 br_if 0 end drop)) "type mismatch")
(assert_invalid (module (func (param i64) (result i32) local.get 0 return)) "type mismatch")
(assert_invalid (module (func (param i64) (result i32) i32.const 1 if (result i32) i32.const 2 else local.get 0 end)) "type mismatch")
(assert_invalid (module (func (param i32) local.get 0 i32.const 1 if (param i32) (result i64) unreachable end drop)) "type mismatch")
(assert_invalid (module (func (param i32 i64) local.get 0 loop (param i32) local.get 1 br 0 end)) "type mismatch")
(assert_invalid (module (func block (result i32) block (result i64) unreachable i32.const 9 i32.const 0 br_table 0 1 end drop unreachable end drop)) "type mismatch")
(assert_invalid (module (memory 1) (func (param i64) local.get 0 i32.load drop)) "type mismatch")
(assert_invalid (module (memory 1) (func (param i64) i32.const 0 local.get 0 i32.store)) "type mismatch")
(assert_invalid (module (func (param funcref externref) local.get 0 local.set 1)) "type mismatch")
(assert_invalid (module (func (param funcref funcref) (result funcref) local.get 0 local.get 1 i32.const 0 select)) "type mismatch")
