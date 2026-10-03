use std::collections::BTreeMap;
use std::fmt::Write;

const CAPACITY: usize = 131_072;
static mut INPUT: [u8; CAPACITY] = [0; CAPACITY];
static mut OUTPUT: [u8; CAPACITY] = [0; CAPACITY];
static mut OUTPUT_LENGTH: usize = 0;
static mut SUM: i64 = 0;
static mut UNIQUE: usize = 0;
static mut MEDIAN: i64 = 0;

#[no_mangle]
pub extern "C" fn buffer_ptr() -> *mut u8 {
    &raw mut INPUT as *mut u8
}

#[no_mangle]
pub extern "C" fn buffer_capacity() -> usize {
    CAPACITY
}

// A library-style telemetry summary: parse signed readings, sort them, count
// each distinct value, and format a deterministic histogram. No host I/O.
#[no_mangle]
pub unsafe extern "C" fn analyze(length: usize) -> i32 {
    OUTPUT_LENGTH = 0;
    SUM = 0;
    UNIQUE = 0;
    MEDIAN = 0;
    if length > CAPACITY {
        return -3;
    }
    let bytes = std::slice::from_raw_parts(&raw const INPUT as *const u8, length);
    let text = match std::str::from_utf8(bytes) {
        Ok(text) => text,
        Err(_) => return -1,
    };
    let mut values = Vec::new();
    for token in text.split_ascii_whitespace() {
        match token.parse::<i64>() {
            Ok(value) => values.push(value),
            Err(_) => return -2,
        }
    }
    if values.is_empty() {
        return -4;
    }
    values.sort_unstable();
    let mut counts = BTreeMap::new();
    let mut sum = 0i64;
    for &value in &values {
        sum = sum.wrapping_add(value);
        *counts.entry(value).or_insert(0u32) += 1;
    }
    let mut output = String::new();
    for (&value, &count) in &counts {
        writeln!(&mut output, "{value}:{count}").unwrap();
    }
    if output.len() > CAPACITY {
        return -5;
    }
    std::ptr::copy_nonoverlapping(output.as_ptr(), &raw mut OUTPUT as *mut u8, output.len());
    OUTPUT_LENGTH = output.len();
    SUM = sum;
    UNIQUE = counts.len();
    MEDIAN = values[values.len() / 2];
    0
}

#[no_mangle]
pub unsafe extern "C" fn result_sum() -> i64 { SUM }

#[no_mangle]
pub unsafe extern "C" fn result_unique() -> usize { UNIQUE }

#[no_mangle]
pub unsafe extern "C" fn result_median() -> i64 { MEDIAN }

#[no_mangle]
pub extern "C" fn output_ptr() -> *const u8 { &raw const OUTPUT as *const u8 }

#[no_mangle]
pub unsafe extern "C" fn output_len() -> usize { OUTPUT_LENGTH }

#[no_mangle]
pub unsafe extern "C" fn input_byte(index: usize) -> u32 {
    // Rust's bounds check must abort for an out-of-range index.
    INPUT[index] as u32
}
