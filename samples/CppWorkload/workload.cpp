// T02 C++ workload: classes, virtual dispatch, templates, lambdas, libc++ algorithms,
// operator new/delete, and a static constructor. Built as a library (reactor) module.
#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <new>
#include <memory>
#include <numeric>

// Freestanding-style allocation: malloc from wasi-libc, trap instead of throwing.
// Keeps libc++abi's stdio-based abort path (and its WASI imports) out of the module.
void *operator new(std::size_t size) {
  if (void *p = std::malloc(size ? size : 1)) return p;
  __builtin_trap();
}
void *operator new[](std::size_t size) { return ::operator new(size); }
void *operator new(std::size_t size, const std::nothrow_t &) noexcept { return std::malloc(size ? size : 1); }
void *operator new[](std::size_t size, const std::nothrow_t &) noexcept { return std::malloc(size ? size : 1); }
void operator delete(void *p) noexcept { std::free(p); }
void operator delete[](void *p) noexcept { std::free(p); }
void operator delete(void *p, std::size_t) noexcept { std::free(p); }
void operator delete[](void *p, std::size_t) noexcept { std::free(p); }
void operator delete(void *p, const std::nothrow_t &) noexcept { std::free(p); }
void operator delete[](void *p, const std::nothrow_t &) noexcept { std::free(p); }
// Same for a pure virtual call: trap instead of libc++abi's abort_message (stderr).
extern "C" [[noreturn]] void __cxa_pure_virtual() { __builtin_trap(); }

namespace {

constexpr int kCapacity = 4096;
alignas(16) int32_t buffer[kCapacity];
int live_objects = 0;
volatile int32_t registry_seed = 1000;  // volatile: forces a dynamic initializer

// Dynamic initialization: runs from __wasm_call_ctors (via _initialize).
struct Registry {
  int32_t base;
  uint32_t calls;
  Registry() : base(registry_seed + static_cast<int32_t>(sizeof(void *))), calls(0) {}
  int32_t next() { return base + static_cast<int32_t>(++calls); }
};
Registry registry;

int clamp_count(int n) { return n < 0 ? 0 : (n > kCapacity ? kCapacity : n); }

class Shape {
 public:
  Shape() { ++live_objects; }
  virtual ~Shape() { --live_objects; }
  virtual int64_t area() const = 0;   // in 1/16 units
  virtual int kind() const = 0;
};
class Rect final : public Shape {
  int32_t w_, h_;
 public:
  Rect(int32_t w, int32_t h) : w_(w), h_(h) {}
  int64_t area() const override { return int64_t{w_} * h_ * 16; }
  int kind() const override { return 1; }
};
class Tri final : public Shape {
  int32_t b_, h_;
 public:
  Tri(int32_t b, int32_t h) : b_(b), h_(h) {}
  int64_t area() const override { return int64_t{b_} * h_ * 8; }
  int kind() const override { return 2; }
};
class Circle final : public Shape {
  int32_t r_;
 public:
  explicit Circle(int32_t r) : r_(r) {}
  int64_t area() const override { return int64_t{r_} * r_ * 50; }  // ~pi*16
  int kind() const override { return 3; }
};

template <typename T, int Rows, int Cols>
struct Matrix {
  std::array<T, Rows * Cols> v{};
  T &at(int r, int c) { return v[r * Cols + c]; }
  const T &at(int r, int c) const { return v[r * Cols + c]; }
};

template <typename T, int N>
Matrix<T, N, N> multiply(const Matrix<T, N, N> &a, const Matrix<T, N, N> &b) {
  Matrix<T, N, N> out;
  for (int i = 0; i < N; ++i)
    for (int j = 0; j < N; ++j) {
      T sum{};
      for (int k = 0; k < N; ++k) sum += a.at(i, k) * b.at(k, j);
      out.at(i, j) = sum;
    }
  return out;
}

uint32_t fold(uint64_t h) { return static_cast<uint32_t>(h ^ (h >> 32)); }

}  // namespace

extern "C" {

__attribute__((export_name("buffer_ptr"))) int32_t *buffer_ptr() { return buffer; }

__attribute__((export_name("registry_next"))) int32_t registry_next() { return registry.next(); }

__attribute__((export_name("pages"))) int32_t pages() { return static_cast<int32_t>(__builtin_wasm_memory_size(0)); }

__attribute__((export_name("live"))) int32_t live() { return live_objects; }

// xorshift32 into the buffer.
__attribute__((export_name("fill"))) int32_t fill(uint32_t seed, int n) {
  n = clamp_count(n);
  uint32_t x = seed ? seed : 0x9e3779b9u;
  for (int i = 0; i < n; ++i) {
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    buffer[i] = static_cast<int32_t>(x);
  }
  return n;
}

// std::sort / std::stable_sort with lambdas; returns an order-sensitive hash.
__attribute__((export_name("sort_values"))) uint32_t sort_values(int n, int mode) {
  n = clamp_count(n);
  int32_t *first = buffer, *last = buffer + n;
  switch (mode) {
    case 0: std::sort(first, last); break;
    case 1: std::sort(first, last, [](int32_t a, int32_t b) { return a > b; }); break;
    case 2: std::stable_sort(first, last, [](int32_t a, int32_t b) { return (a & 0xff) < (b & 0xff); }); break;
    case 3: std::reverse(first, last); break;
    default: return 0xffffffffu;
  }
  uint64_t h = 1469598103934665603ull;
  for (int i = 0; i < n; ++i) { h ^= static_cast<uint32_t>(buffer[i]); h *= 1099511628211ull; }
  return fold(h);
}

// Accumulate and partial-sum with libc++ <numeric>; writes prefix sums back.
__attribute__((export_name("prefix"))) int64_t prefix(int n) {
  n = clamp_count(n);
  std::partial_sum(buffer, buffer + n, buffer, [](int32_t a, int32_t b) {
    return static_cast<int32_t>(static_cast<uint32_t>(a) + static_cast<uint32_t>(b));
  });
  return std::accumulate(buffer, buffer + n, int64_t{0});
}

// Heap-allocated polymorphic objects; n controls allocations (may grow memory).
__attribute__((export_name("shapes"))) int64_t shapes(int n, int scale) {
  if (n < 0) return -1;
  auto list = std::make_unique<std::unique_ptr<Shape>[]>(static_cast<size_t>(n));
  for (int i = 0; i < n; ++i) {
    int32_t a = (i % 17) + (scale & 0xffff), b = (i % 5) + 1;
    switch (i % 3) {
      case 0: list[i] = std::make_unique<Rect>(a, b); break;
      case 1: list[i] = std::make_unique<Tri>(a, b); break;
      default: list[i] = std::make_unique<Circle>(a); break;
    }
  }
  int64_t total = 0;
  for (int i = 0; i < n; ++i) total += list[i]->area() * list[i]->kind();
  return total + live_objects;  // all n objects are alive here
}

// Template matrix power over i64 with wrapping arithmetic.
__attribute__((export_name("matrix"))) uint32_t matrix(int32_t seed, int power) {
  Matrix<uint64_t, 4, 4> m;
  for (int i = 0; i < 16; ++i) m.v[i] = (static_cast<uint64_t>(static_cast<int64_t>(seed)) * (i + 1) + i) | 1;
  Matrix<uint64_t, 4, 4> acc;
  for (int i = 0; i < 4; ++i) acc.at(i, i) = 1;
  for (int p = 0; p < power && p < 64; ++p) {
    acc = multiply(acc, m);
    acc.at(p & 3, (p >> 2) & 3) ^= 0x9e3779b97f4a7c15ull + static_cast<uint64_t>(p);  // keep entries from collapsing to 0 mod 2^64
  }
  uint64_t h = 0;
  for (uint64_t value : acc.v) h = h * 31 + value;
  return fold(h);
}

// Bounds-checked element read; traps (unreachable) on an out-of-range index.
__attribute__((export_name("checked_at"))) int32_t checked_at(int index) {
  if (index < 0 || index >= kCapacity) __builtin_trap();
  return buffer[index];
}

// Signed division: traps on division by zero and INT_MIN / -1, as WASM i32.div_s does.
__attribute__((export_name("divide"))) int32_t divide(int32_t a, int32_t b) { return a / b; }

}  // extern "C"
