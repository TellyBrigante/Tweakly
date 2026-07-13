//*********************************************************
//
// Copyright (c) Microsoft. All rights reserved.
// Modifications Copyright (c) Tweakly contributors.
// Licensed under the MIT License.
//
//*********************************************************

#pragma once

namespace BenchmarkOutput
{
    inline std::string Utf8(const wchar_t* value)
    {
        if (value == nullptr || *value == L'\0')
        {
            return {};
        }

        int byteCount = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
        if (byteCount <= 1)
        {
            return {};
        }

        std::string result(static_cast<size_t>(byteCount), '\0');
        WideCharToMultiByte(CP_UTF8, 0, value, -1, &result[0], byteCount, nullptr, nullptr);
        result.resize(static_cast<size_t>(byteCount - 1));
        return result;
    }

    inline void WriteLine(const std::string& text, DWORD standardHandle = STD_OUTPUT_HANDLE)
    {
        HANDLE handle = GetStdHandle(standardHandle);
        if (handle == nullptr || handle == INVALID_HANDLE_VALUE)
        {
            return;
        }

        std::string line = text + "\r\n";
        DWORD written = 0;
        WriteFile(handle, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
    }
}
