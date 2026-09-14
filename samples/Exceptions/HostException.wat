(module
  (import "env" "callback" (func $callback (param i32)))
  (tag $value (param i32))
  (func (export "f") (param i32)
    try
      local.get 0
      call $callback
    catch_all
    end))
