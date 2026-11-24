#include "../../tools/companion/native_field_of_view.h"
#include <cassert>
#include <cmath>
#include <cstdio>
#include <vector>

static void WriteRequest(const std::wstring& output, const char* text) {
    FILE* file = nullptr;
    assert(!_wfopen_s(&file,(output+L"\\field-of-view.txt").c_str(),L"wb") && file);
    fputs(text,file);
    fclose(file);
}

static bool Near(double a, double b) { return std::fabs(a-b) < 1e-4; }

int main(int argc, char** argv) {
    using namespace NativeFieldOfView;
    assert(argc > 1);
    std::wstring output(argv[1],argv[1]+strlen(argv[1]));
    DeleteFileW((output+L"\\field-of-view.txt").c_str());
    assert(Read(output) == 0);
    WriteRequest(output,"default\n");
    assert(Read(output) == 0);
    WriteRequest(output,"75\n");
    assert(Read(output) == 75);
    WriteRequest(output,"50\n");
    assert(Read(output) == 50);
    WriteRequest(output,"100\n");
    assert(Read(output) == 100);
    for (const char* bad : {"49\n","101\n","75.5\n","wide\n","\n","-80\n"}) {
        WriteRequest(output,bad);
        assert(Read(output) == -1);
    }
    DeleteFileW((output+L"\\field-of-view.txt").c_str());

    // A fake image whose settings pointer is at +0x10 and whose default angle is at settings+0x20.
    std::vector<BYTE> image(0x100);
    float settings[16]{};
    BYTE* settingsPointer = reinterpret_cast<BYTE*>(settings);
    memcpy(image.data()+0x10,&settingsPointer,sizeof(settingsPointer));
    const Layout fake{0,nullptr,0,nullptr,0,0x10,0x20};
    Image = image.data(); Active = &fake;

    static constexpr double Pi = 3.14159265358979323846;
    const auto half = [](double degrees) { return float(degrees*Pi/360.0); };
    const auto degrees = [](float half) { return double(half)*360.0/Pi; };

    // The game's default view gets the requested angle.
    settings[8] = 59.84f;
    Requested = 75;
    assert(Near(degrees(Scale(half(59.84))),75.0));
    // A zoomed view keeps its zoom relative to the default view.
    const double zoom = std::tan(half(20.0))/std::tan(half(59.84));
    assert(Near(std::tan(Scale(half(20.0)))/std::tan(Scale(half(59.84))),zoom));
    // Narrower than the default.
    Requested = 50;
    assert(Near(degrees(Scale(half(59.84))),50.0));
    // A different base from the game's settings.
    settings[8] = 70.0f;
    Requested = 90;
    assert(Near(degrees(Scale(half(70.0))),90.0));
    // No settings object, or an unusable value: the game's own fallback, 59.84.
    settings[8] = 0.0f;
    assert(Near(degrees(Scale(half(59.84))),90.0));
    BYTE* none = nullptr;
    memcpy(image.data()+0x10,&none,sizeof(none));
    assert(Near(degrees(Scale(half(59.84))),90.0));
    // Out-of-range angles pass through, and the result stays below 85 degrees.
    assert(Scale(0.0f) == 0.0f);
    assert(Scale(-0.5f) == -0.5f);
    assert(Scale(1.55f) == 1.55f);
    Requested = 100;
    assert(Scale(1.4f) <= float(85.0*Pi/180.0)+1e-6f);

    // Installation needs matching code and a valid request.
    assert(Install(image.data(),fake,0) && !Verified && !strcmp(Error,"notRequested"));
    assert(!Install(image.data(),fake,120) && !strcmp(Error,"invalidRequest"));
    const BYTE expected[]{0x55,0x8b,0xec};
    const Signature signatures[]{{0x40,expected,sizeof(expected)}};
    const Layout mismatched{0x40,signatures,1,nullptr,0,0x10,0x20};
    assert(!Install(image.data(),mismatched,75) && !strcmp(Error,"codeMismatch"));
    memcpy(image.data()+0x40,expected,sizeof(expected));
    assert(CodeMatches(image.data(),mismatched));
    // A listed call must reach the perspective routine.
    const unsigned returns[]{0x30};
    const Layout calls{0x40,signatures,1,returns,1,0x10,0x20};
    assert(!CodeMatches(image.data(),calls));
    image[0x2b] = 0xe8;
    const LONG delta = 0x40-0x30;
    memcpy(image.data()+0x2c,&delta,sizeof(delta));
    assert(CodeMatches(image.data(),calls));
    std::puts("field_of_view_test ok");
    return 0;
}
