#![no_std]

#[panic_handler]
fn panic(_info: &core::panic::PanicInfo) -> ! {
    loop {}
}

#[no_mangle]
pub extern "C" fn add_i64(a: i64, b: i64) -> i64 {
    a.wrapping_add(b)
}

#[no_mangle]
pub unsafe extern "C" fn sum_u8(bytes: *const u8, length: usize) -> u32 {
    let mut result: u32 = 0;
    let mut i: usize = 0;
    while i < length {
        result = result.wrapping_add(*bytes.add(i) as u32);
        i += 1;
    }
    result
}
