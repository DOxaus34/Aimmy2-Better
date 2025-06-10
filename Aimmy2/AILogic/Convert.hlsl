//
// Copyright (c) 2024 Baby Hamsta
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//

// Input texture (BGRA)
Texture2D<float4> InputTexture : register(t0);

// Output buffer (Planar RGB float)
RWBuffer<float> OutputBuffer : register(u0);

// Thead group size
[numthreads(16, 16, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    // Image dimensions are known to be 320x320
    const int width = 320;
    const int height = 320;

    // Prevent out-of-bounds access
    if (dispatchThreadID.x >= width || dispatchThreadID.y >= height)
    {
        return;
    }

    // Read the BGRA pixel from the input texture
    float4 pixel = InputTexture.Load(dispatchThreadID);

    // Calculate the base index for the current pixel in the 1D output buffer
    int base_idx = dispatchThreadID.y * width + dispatchThreadID.x;
    
    // Calculate the offset for each color plane
    int plane_size = width * height;

    // Write the R, G, B components to their respective planes in the output buffer
    // The input is BGRA, so we need to swizzle:
    // Red   is pixel.z
    // Green is pixel.y
    // Blue  is pixel.x
    OutputBuffer[base_idx + (2 * plane_size)] = pixel.z; // R plane
    OutputBuffer[base_idx + plane_size] = pixel.y;     // G plane
    OutputBuffer[base_idx] = pixel.x;                  // B plane
} 