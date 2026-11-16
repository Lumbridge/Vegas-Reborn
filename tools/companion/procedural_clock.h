#pragma once
#include <cstdint>
#include <cstring>

namespace ProceduralClock {
// A presentation compatibility rate, not a recovered retail timing constant.
constexpr unsigned Rate = 60;
constexpr unsigned TapeSize = 16;
struct Replay {
    bool valid = false;
    std::uint64_t step = 0;
    unsigned count = 0, cursor = 0;
    int tape[TapeSize]{};
    unsigned char before[80]{};

    void Begin(std::uint64_t current, void* history, unsigned bytes) {
        if (!valid || current != step) {
            valid = true;
            step = current;
            count = 0;
            std::memcpy(before, history, bytes);
        } else {
            std::memcpy(history, before, bytes);
        }
        cursor = 0;
    }
    template<class Random> int Next(Random random) {
        if (cursor < count) return tape[cursor++];
        const int value = random();
        if (count < TapeSize) {
            tape[count++] = value;
            ++cursor;
        }
        return value;
    }
};
inline std::uint64_t Step(std::uint64_t ticks, std::uint64_t frequency) {
    // Avoid multiplying the complete long-running QPC counter by Rate.
    return (ticks / frequency) * Rate + (ticks % frequency) * Rate / frequency;
}
}
